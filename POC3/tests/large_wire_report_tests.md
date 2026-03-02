# LargeWireReport — V2 Test Plan

## Job Info
- **V2 Config**: `large_wire_report_v2.json`
- **Tier**: 1 (Framework Only)
- **External Module**: None (V1 External `LargeWireReportBuilder.cs` eliminated per AP3)

## Pre-Conditions

1. PostgreSQL is accessible at `172.18.0.1` with `claude/claude` credentials, database `atc`.
2. Tables `datalake.wire_transfers` and `datalake.customers` contain data for the effective date range starting `2024-10-01`.
3. V1 baseline output exists at `Output/curated/large_wire_report.csv` (produced by V1 config `large_wire_report.json`).
4. V2 config `large_wire_report_v2.json` is deployed to `JobExecutor/Jobs/`.
5. Proofmark config `POC3/proofmark_configs/large_wire_report.yaml` is deployed.
6. The `Output/double_secret_curated/` directory exists or will be created by the framework.

## Test Cases

### TC-1: Output Schema Validation

**Objective**: Verify the V2 output CSV has exactly the correct columns in the correct order.

**Expected schema** (9 columns, in order):
| # | Column | Type |
|---|--------|------|
| 1 | wire_id | integer |
| 2 | customer_id | integer |
| 3 | first_name | string |
| 4 | last_name | string |
| 5 | direction | string |
| 6 | amount | numeric (2 dp) |
| 7 | counterparty_name | string |
| 8 | status | string |
| 9 | as_of | date |

**Steps**:
1. Run V2 job: `dotnet run --project JobExecutor -- LargeWireReportV2`
2. Read the first line of `Output/double_secret_curated/large_wire_report.csv`.
3. Confirm header is: `wire_id,customer_id,first_name,last_name,direction,amount,counterparty_name,status,as_of`
4. Compare header to V1 output at `Output/curated/large_wire_report.csv` — must be identical.

**Pass criteria**: Column names and column order match V1 exactly.

### TC-2: Row Count Equivalence

**Objective**: Verify V2 produces the same number of data rows as V1.

**Steps**:
1. Count data rows (excluding header) in V1 output: `wc -l Output/curated/large_wire_report.csv` minus 1.
2. Count data rows (excluding header) in V2 output: `wc -l Output/double_secret_curated/large_wire_report.csv` minus 1.
3. Compare counts.

**Pass criteria**: Row counts are identical. Any difference indicates a problem with the `amount > 10000` filter (BR-1), the customer JOIN, or the effective date range.

### TC-3: Data Content Equivalence

**Objective**: Verify V2 output data matches V1 byte-for-byte (accounting for W5 rounding risk).

**Steps**:
1. Run `diff Output/curated/large_wire_report.csv Output/double_secret_curated/large_wire_report.csv`.
2. If differences exist, inspect whether they are in the `amount` column (potential W5 banker's rounding divergence at .XX5 midpoints).
3. For any amount-column differences, verify whether the source amount is an exact .XX5 midpoint value.

**W-code note (W5)**: SQLite `ROUND()` uses "round half away from zero" while V1 uses `MidpointRounding.ToEven`. Differences will only appear for amounts at exact .XX5 midpoints. If any are found, this is a known risk documented in the FSD and triggers escalation to Tier 2.

**Pass criteria**: Files are byte-identical, OR any differences are exclusively in the `amount` column at .XX5 midpoint values (W5 escalation path).

### TC-4: Writer Configuration

**Objective**: Verify the CsvFileWriter config matches V1 behavior.

| Property | Expected Value | Verification Method |
|----------|---------------|---------------------|
| type | CsvFileWriter | Inspect V2 JSON config |
| source | `output` | Inspect V2 JSON config |
| outputFile | `Output/double_secret_curated/large_wire_report.csv` | Confirm file is written to this path |
| includeHeader | `true` | First line of output is column names |
| writeMode | `Overwrite` | Run job twice; second run's output replaces first (file modified timestamp updates, content from second run only) |
| lineEnding | `LF` | `xxd` or `od` on output file to confirm `\n` (0x0A) line endings, no `\r` (0x0D) |
| trailerFormat | not specified | No trailer row present at end of file |

**Steps**:
1. Inspect `large_wire_report_v2.json` writer section for all properties above.
2. Run the job and verify the output file location.
3. Check line endings: `file Output/double_secret_curated/large_wire_report.csv` should report "ASCII text" (not "CRLF").
4. Confirm no trailer: last line of file should be a data row, not a summary/count/date row.

**Pass criteria**: All writer properties match V1 configuration. Output path differs only in the directory (`double_secret_curated` vs `curated`).

### TC-5: Anti-Pattern Elimination Verification

**Objective**: Confirm all identified anti-patterns are eliminated in V2.

| AP Code | Anti-Pattern | Verification |
|---------|-------------|--------------|
| AP3 | Unnecessary External module | V2 JSON config has NO module with `"type": "External"`. Module chain is `DataSourcing -> DataSourcing -> Transformation -> CsvFileWriter`. |
| AP6 | Row-by-row iteration | No External module means no `foreach` loop. All logic is in the SQL Transformation (set-based). |
| AP7 | Magic values | SQL contains `10000` with inline comment `/* BR-1: regulatory wire threshold */` or equivalent documentation. |

**Steps**:
1. Parse `large_wire_report_v2.json` and confirm no `"type": "External"` entries.
2. Confirm exactly 4 modules: 2x DataSourcing, 1x Transformation, 1x CsvFileWriter.
3. Inspect the Transformation SQL string for the `10000` threshold and verify it has contextual documentation (inline comment or FSD reference).

**Pass criteria**: No External module in V2 config. All three AP codes are demonstrably eliminated.

### TC-6: Edge Cases

#### TC-6a: $10,000 Exact Threshold (BR-1)

**Objective**: Confirm wires with exactly $10,000.00 are excluded (strict greater-than).

**Steps**:
1. Query source data for wires at exactly 10000.00: `SELECT COUNT(*) FROM datalake.wire_transfers WHERE amount = 10000`.
2. If any exist, confirm they are absent from V2 output.
3. Verify the SQL uses `> 10000` not `>= 10000`.

**Pass criteria**: No wire with `amount = 10000.00` appears in output.

#### TC-6b: Unknown Customer (BR-2, BR-7)

**Objective**: Confirm wires with customer_ids not present in `customers` table produce empty-string names.

**Steps**:
1. Identify a customer_id in wire_transfers that has no match in customers (if any exist in the data).
2. Find the corresponding row(s) in V2 output.
3. Confirm `first_name` and `last_name` are empty strings (not NULL, not "null", not missing).

**Pass criteria**: Unmatched customer_ids produce empty strings for first_name and last_name. Wire is still included in output.

#### TC-6c: NULL Customer Names (BR-2)

**Objective**: Confirm NULL first_name or last_name values in the customers table are coalesced to empty strings.

**Steps**:
1. Query: `SELECT id, first_name, last_name FROM datalake.customers WHERE first_name IS NULL OR last_name IS NULL`.
2. If any matched customer_ids appear in large wires, verify their output names are empty strings.

**Pass criteria**: NULL source names become empty strings in output.

#### TC-6d: Customer Deduplication — Last-Write-Wins (BR-7)

**Objective**: Confirm that when a customer has multiple snapshot rows (different as_of dates), the name from the latest as_of wins.

**Steps**:
1. Query for customers with name changes across dates: `SELECT id, first_name, last_name, as_of FROM datalake.customers WHERE id IN (SELECT DISTINCT customer_id FROM datalake.wire_transfers WHERE amount > 10000) ORDER BY id, as_of`.
2. For any customer where first_name or last_name differs across as_of dates, note the latest-as_of values.
3. In V2 output, confirm those customer_ids use the name from the latest as_of.

**Pass criteria**: Customer names match the latest-as_of-date version, consistent with V1's dictionary-overwrite behavior.

#### TC-6e: Empty Wire Transfers (BR-6)

**Objective**: Verify behavior when no wire transfers exceed the threshold.

**Steps**:
1. This is a theoretical edge case — unlikely with the actual dataset (amounts range ~$1,012–$49,959).
2. If testable with a synthetic date range where no wires > $10,000 exist, run the job for that date.
3. Expected: either a header-only CSV file (if wire_transfers table is populated but all amounts <= $10,000) or a framework error (if wire_transfers table is empty and SQLite table is not registered).

**Pass criteria**: Behavior matches V1. If wire_transfers has rows but none > $10,000, output is header-only. Note: FSD documents a known risk if wire_transfers is completely empty (SQL fails on missing table).

### TC-7: Proofmark Configuration

**Objective**: Validate the Proofmark YAML config is correct and complete.

**Expected config** (`POC3/proofmark_configs/large_wire_report.yaml`):
```yaml
comparison_target: "large_wire_report"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

**Verification checklist**:
| Field | Expected | Rationale |
|-------|----------|-----------|
| comparison_target | `"large_wire_report"` | Matches job/output name |
| reader | `csv` | Output is CsvFileWriter |
| threshold | `100.0` | Strict match — all columns deterministic per BRD |
| csv.header_rows | `1` | `includeHeader: true` in writer config |
| csv.trailer_rows | `0` | No trailerFormat specified |
| columns.excluded | (none) | No non-deterministic fields per BRD |
| columns.fuzzy | (none) | No floating-point computation that warrants tolerance (W5 risk accepted at strict level; failures trigger Tier 2 escalation) |

**Pass criteria**: YAML file matches expected config exactly. No excluded or fuzzy columns.

## W-Code Test Cases

### TC-W5: Banker's Rounding

**Objective**: Verify the impact of W5 (banker's rounding) on V2 output.

**Background**: V1 uses `Math.Round(amount, 2, MidpointRounding.ToEven)` (banker's rounding). V2 uses SQLite `ROUND(amount, 2)` which rounds half away from zero. These produce different results only at exact .XX5 midpoints.

**Steps**:
1. Query for wire amounts that are exact .XX5 midpoints after the threshold filter:
   ```sql
   SELECT wire_id, amount
   FROM datalake.wire_transfers
   WHERE amount > 10000
     AND (amount * 1000) % 10 = 5
     AND (amount * 100) % 1 != 0
   ```
2. If no such values exist, W5 is a non-issue for this dataset — ROUND behaviors are identical for non-midpoint values.
3. If midpoint values exist, compute expected V1 output (banker's rounding) vs V2 output (half-away-from-zero) for each.
4. Run Proofmark comparison. Any failures on the `amount` column at these rows confirm the W5 divergence.

**Escalation path**: If W5 causes any Proofmark failure, the FSD prescribes escalation to Tier 2 with a minimal External module that applies `Math.Round(amount, 2, MidpointRounding.ToEven)` post-SQL.

**Pass criteria (nominal)**: No .XX5 midpoint amounts exist in the filtered dataset, making W5 moot. OR: midpoint amounts exist but both rounding methods produce the same result (e.g., the digit before the midpoint is even).

**Pass criteria (escalation)**: If divergence is detected, document the affected rows and escalate to Tier 2 per FSD risk register.

## Notes

1. **Output path difference**: V1 writes to `Output/curated/`, V2 writes to `Output/double_secret_curated/`. This is by design per POC3 spec and is NOT a defect.
2. **No ORDER BY in SQL**: V1 does not sort output. V2 SQL has no ORDER BY. Row order depends on DataSourcing's default `ORDER BY as_of`. If Proofmark does order-sensitive comparison, row ordering must match. If it does not, consider adding `ORDER BY wt.as_of, wt.wire_id` to the SQL to guarantee deterministic ordering.
3. **customer_id CAST**: The SQL uses `CAST(wt.customer_id AS INTEGER)` to match V1's `Convert.ToInt32()`. Verify that no customer_id values are non-integer (would cause CAST failure).
4. **Multi-date Overwrite**: Since writeMode is Overwrite and the job runs across an effective date range, only the final date's execution output survives. Proofmark comparison should be run after the full date range completes, comparing the final output files.
5. **as_of column source**: The `as_of` in output comes from `wire_transfers.as_of`, not from `customers.as_of` or `__maxEffectiveDate`. Verify this is correct by cross-referencing a few rows.
