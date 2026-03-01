# SuspiciousWireFlags — Business Requirements Document

## Overview
Flags wire transfers that are suspicious based on two criteria: counterparty name containing "OFFSHORE" or transfer amount exceeding 50,000. Produces a filtered list of flagged wires with the reason for flagging.

## Output Type
ParquetFileWriter

## Writer Configuration
- **outputDirectory**: `Output/curated/suspicious_wire_flags/`
- **numParts**: 1
- **writeMode**: Overwrite

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.wire_transfers | wire_id, customer_id, direction, amount, counterparty_name, counterparty_bank, status | Effective date range via executor; filtered by OFFSHORE counterparty or amount > 50000 | [suspicious_wire_flags.json:4-11] |
| datalake.accounts | account_id, customer_id, account_type, account_status | Effective date range via executor | [suspicious_wire_flags.json:12-19] |
| datalake.customers | id, first_name, last_name, suffix | Effective date range via executor | [suspicious_wire_flags.json:20-27] |

### Source Table Schemas (from database)

**wire_transfers**: wire_id (integer), customer_id (integer), account_id (integer), direction (varchar), amount (numeric), counterparty_name (varchar), counterparty_bank (varchar), status (varchar), wire_timestamp (timestamp), as_of (date)

**accounts**: account_id (integer), customer_id (integer), account_type (varchar), account_status (varchar), open_date (date), current_balance (numeric), interest_rate (numeric), credit_limit (numeric), apr (numeric), as_of (date)

**customers**: id (integer), prefix (varchar), first_name (varchar), last_name (varchar), sort_name (varchar), suffix (varchar), birthdate (date), as_of (date)

## Business Rules

BR-1: A wire is flagged as "OFFSHORE_COUNTERPARTY" if its counterparty_name contains the substring "OFFSHORE" (case-sensitive, using String.Contains).
- Confidence: HIGH
- Evidence: [SuspiciousWireFlagProcessor.cs:35] — `counterpartyName.Contains("OFFSHORE")`

BR-2: A wire is flagged as "HIGH_AMOUNT" if its amount is strictly greater than 50,000 AND it was NOT already flagged as OFFSHORE_COUNTERPARTY. The conditions are mutually exclusive via else-if — a wire can only receive one flag.
- Confidence: HIGH
- Evidence: [SuspiciousWireFlagProcessor.cs:39-41] — `else if (amount > 50000)` — the `else` makes it mutually exclusive with the OFFSHORE check

BR-3: Wires that match neither condition are excluded from the output entirely.
- Confidence: HIGH
- Evidence: [SuspiciousWireFlagProcessor.cs:43-56] — `if (flagReason != null)` guards the output row creation

BR-4: The `accounts` DataFrame is sourced but never used in the External module logic. It is a dead-end data source.
- Confidence: HIGH
- Evidence: [SuspiciousWireFlagProcessor.cs:18-19] — comment "AP1: accounts sourced but never used (dead-end)"; no reference to accounts in any computation

BR-5: The `customers` DataFrame is sourced but never used in the External module logic. It is a dead-end data source.
- Confidence: HIGH
- Evidence: [SuspiciousWireFlagProcessor.cs:19] — comment "AP4: unused columns — counterparty_bank from wire_transfers, suffix from customers"; customers is not referenced in any computation

BR-6: The `counterparty_bank` column is sourced from wire_transfers but excluded from the output schema. It is an unused column.
- Confidence: HIGH
- Evidence: [SuspiciousWireFlagProcessor.cs:19] — comment about unused counterparty_bank; output columns list does not include it

BR-7: The `suffix` column is sourced from customers but never used.
- Confidence: HIGH
- Evidence: [SuspiciousWireFlagProcessor.cs:19]; customers DataFrame is not used at all

BR-8: NULL counterparty_name is coalesced to empty string, meaning NULL counterparties will never match the "OFFSHORE" check.
- Confidence: HIGH
- Evidence: [SuspiciousWireFlagProcessor.cs:31] — `?.ToString() ?? ""`

BR-9: If wire_transfers is null or empty, an empty output DataFrame is produced.
- Confidence: HIGH
- Evidence: [SuspiciousWireFlagProcessor.cs:21-25]

BR-10: Current data contains no counterparty names with "OFFSHORE" substring and no wire amounts exceeding 50,000 (max is 49,959). This means the current output will be empty.
- Confidence: HIGH
- Evidence: [DB query: 0 rows with OFFSHORE counterparty]; [DB query: 0 rows with amount > 50000]; [DB query: MAX(amount) = 49959]

BR-11: The flag priority is OFFSHORE_COUNTERPARTY > HIGH_AMOUNT. A wire matching both conditions receives only OFFSHORE_COUNTERPARTY.
- Confidence: HIGH
- Evidence: [SuspiciousWireFlagProcessor.cs:35-41] — if/else-if structure

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| wire_id | wire_transfers.wire_id | Direct passthrough | [SuspiciousWireFlagProcessor.cs:48] |
| customer_id | wire_transfers.customer_id | Direct passthrough | [SuspiciousWireFlagProcessor.cs:49] |
| direction | wire_transfers.direction | Direct passthrough | [SuspiciousWireFlagProcessor.cs:50] |
| amount | wire_transfers.amount | Converted to decimal via Convert.ToDecimal | [SuspiciousWireFlagProcessor.cs:32,51] |
| counterparty_name | wire_transfers.counterparty_name | NULL coalesced to "" | [SuspiciousWireFlagProcessor.cs:31,52] |
| status | wire_transfers.status | Direct passthrough | [SuspiciousWireFlagProcessor.cs:53] |
| flag_reason | Computed | "OFFSHORE_COUNTERPARTY" or "HIGH_AMOUNT" | [SuspiciousWireFlagProcessor.cs:37,40,54] |
| as_of | wire_transfers.as_of | Direct passthrough | [SuspiciousWireFlagProcessor.cs:55] |

## Non-Deterministic Fields
None identified. Output row order follows the iteration order of the wire_transfers DataFrame.

## Write Mode Implications
- **Overwrite** mode: each run replaces all part files in the output directory. Multi-day runs retain only the last effective date's output.
- Evidence: [suspicious_wire_flags.json:35]

## Edge Cases

1. **Empty output with current data**: No wires in the current dataset trigger either flag condition (no OFFSHORE counterparties, max amount 49,959 < 50,000).
   - Evidence: [DB queries]

2. **NULL counterparty_name**: Coalesced to "", so NULL counterparties are never flagged as OFFSHORE.
   - Evidence: [SuspiciousWireFlagProcessor.cs:31]

3. **Mutually exclusive flags**: A wire matching both OFFSHORE and HIGH_AMOUNT only gets OFFSHORE_COUNTERPARTY.
   - Evidence: [SuspiciousWireFlagProcessor.cs:35-41]

4. **Case sensitivity**: The OFFSHORE check is case-sensitive. "offshore" or "Offshore" would NOT match.
   - Confidence: HIGH
   - Evidence: [SuspiciousWireFlagProcessor.cs:35] — `String.Contains` without `StringComparison` parameter defaults to ordinal (case-sensitive)

5. **Dead-end data sources**: Both accounts and customers are sourced but entirely unused, wasting memory and query time.
   - Evidence: [SuspiciousWireFlagProcessor.cs:18-19]

6. **Amount exactly 50,000**: Wires with amount = 50,000 are NOT flagged (strictly > 50,000).
   - Evidence: [SuspiciousWireFlagProcessor.cs:39]

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: OFFSHORE counterparty check | [SuspiciousWireFlagProcessor.cs:35] |
| BR-2: HIGH_AMOUNT check (mutually exclusive) | [SuspiciousWireFlagProcessor.cs:39-41] |
| BR-3: Non-matching exclusion | [SuspiciousWireFlagProcessor.cs:43-56] |
| BR-4: Dead-end accounts | [SuspiciousWireFlagProcessor.cs:18-19] |
| BR-5: Dead-end customers | [SuspiciousWireFlagProcessor.cs:18-19] |
| BR-6: Unused counterparty_bank | [SuspiciousWireFlagProcessor.cs:19] |
| BR-7: Unused suffix | [SuspiciousWireFlagProcessor.cs:19] |
| BR-8: NULL counterparty coalescing | [SuspiciousWireFlagProcessor.cs:31] |
| BR-9: Empty input guard | [SuspiciousWireFlagProcessor.cs:21-25] |
| BR-10: Current data yields empty output | [DB queries] |
| BR-11: Flag priority order | [SuspiciousWireFlagProcessor.cs:35-41] |

## Open Questions
1. With current data never triggering either flag, is this job effectively a no-op? Are the thresholds calibrated for the test dataset?
   - Confidence: MEDIUM — the max wire amount is 49,959 (just under 50,000), suggesting the threshold may need adjustment
2. Should the OFFSHORE check be case-insensitive?
   - Confidence: LOW — current data has no OFFSHORE strings at all
