# MerchantCategoryDirectory — Business Requirements Document

## Overview
Produces a directory listing of all merchant category codes with their descriptions and risk levels. This is a simple pass-through/reference data export with no transformation logic beyond selecting the relevant columns.

## Output Type
CsvFileWriter

## Writer Configuration
- **outputFile**: `Output/curated/merchant_category_directory.csv`
- **includeHeader**: true
- **writeMode**: Append
- **lineEnding**: LF
- **trailerFormat**: not configured (no trailer)

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.merchant_categories | mcc_code, mcc_description, risk_level | Effective date range via DataSourcing | [merchant_category_directory.json:8-11], [merchant_category_directory.json:22] SQL SELECT |
| datalake.cards | card_id, customer_id, card_type | Effective date range via DataSourcing; **sourced but never used in SQL transformation** | [merchant_category_directory.json:14-17], [merchant_category_directory.json:22] SQL only references merchant_categories |

## Business Rules

BR-1: The SQL selects all rows from merchant_categories with columns: mcc_code, mcc_description, risk_level, as_of. No filtering or aggregation is applied.
- Confidence: HIGH
- Evidence: [merchant_category_directory.json:22] SQL `SELECT mc.mcc_code, mc.mcc_description, mc.risk_level, mc.as_of FROM merchant_categories mc`

BR-2: The `cards` table is sourced by DataSourcing but never referenced in the SQL transformation. This is dead data sourcing.
- Confidence: HIGH
- Evidence: [merchant_category_directory.json:14-17] sources cards; [merchant_category_directory.json:22] SQL only references `merchant_categories mc`

BR-3: There are 20 distinct MCC codes in the merchant_categories table per as_of date.
- Confidence: HIGH
- Evidence: [DB query: 20 rows per as_of date in merchant_categories]

BR-4: Risk levels are: Low (15 codes), Medium (3 codes: Airlines, Jewelry, ATM/Cash), High (2 codes: Precious Metals, Gambling).
- Confidence: HIGH
- Evidence: [DB query: full merchant_categories listing]

BR-5: The output includes the `as_of` column, meaning each date snapshot's merchant categories are output as separate rows.
- Confidence: HIGH
- Evidence: [merchant_category_directory.json:22] SQL selects `mc.as_of`

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| mcc_code | merchant_categories.mcc_code | Pass-through | [merchant_category_directory.json:22] |
| mcc_description | merchant_categories.mcc_description | Pass-through | [merchant_category_directory.json:22] |
| risk_level | merchant_categories.risk_level | Pass-through | [merchant_category_directory.json:22] |
| as_of | merchant_categories.as_of | Pass-through | [merchant_category_directory.json:22] |

## Non-Deterministic Fields
None identified. All fields are deterministic pass-throughs from source data.

## Write Mode Implications
- **Append** mode: This is the only job in the Card Analytics domain that uses Append mode. Each run APPENDS to the existing CSV file rather than replacing it.
- For multi-day auto-advance runs, each effective date's data is appended to the file, accumulating over time.
- The header is written ONLY on the first run (when the file does not yet exist). Subsequent append runs skip the header. This is because `CsvFileWriter` checks `if (_includeHeader && !append)` where `append` is true when the file already exists.
- Confidence: HIGH
- Evidence: [merchant_category_directory.json:29] `"writeMode": "Append"`; [Lib/Modules/CsvFileWriter.cs:42,47] `var append = _writeMode == WriteMode.Append && File.Exists(resolvedPath)` and `if (_includeHeader && !append)`

## Edge Cases

1. **No duplicate headers in Append mode**: The CsvFileWriter skips header writing when appending to an existing file (`!append` check at line 47). The header appears only on the first run when the file is created.
   - Confidence: HIGH
   - Evidence: [Lib/Modules/CsvFileWriter.cs:47] `if (_includeHeader && !append)`

2. **Merchant categories data includes weekends**: The merchant_categories table has data for all 7 days. Weekend effective dates will return data normally.
   - Confidence: HIGH
   - Evidence: [DB: merchant_categories has weekend as_of dates]

3. **Reference data duplication**: Since this is a daily snapshot and writeMode is Append, the file accumulates the same 20 MCC rows per day. Over 30 days, the file would have 600+ rows (plus headers) of largely identical reference data.
   - Confidence: HIGH
   - Evidence: [DB: 20 rows/day], [merchant_category_directory.json:29] Append mode

4. **Cards data unused but sourced**: The cards DataSourcing runs queries against the database unnecessarily. This wastes processing time but has no effect on output correctness.
   - Confidence: HIGH
   - Evidence: [merchant_category_directory.json:14-17] vs [merchant_category_directory.json:22]

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Simple SELECT, no filter | merchant_category_directory.json:22 (SQL) |
| BR-2: Dead cards sourcing | merchant_category_directory.json:14-17 vs :22 |
| BR-3: 20 MCC codes per date | DB query on merchant_categories |
| BR-4: Risk level distribution | DB query on merchant_categories |
| BR-5: as_of in output | merchant_category_directory.json:22 (SQL) |
| Writer config (Append) | merchant_category_directory.json:26-31 |

## Open Questions
None. The header-in-append-mode question was resolved by reading `Lib/Modules/CsvFileWriter.cs:47` — headers are skipped on appends.
