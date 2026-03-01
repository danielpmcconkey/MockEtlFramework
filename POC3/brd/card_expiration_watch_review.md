# card_expiration_watch — BRD Review

## Reviewer: reviewer-1
## Status: PASS

## Section Checks
| Section | Status | Notes |
|---------|--------|-------|
| Overview | PASS | Accurate summary of 90-day expiration window with weekend fallback |
| Output Type | PASS | Correctly identifies ParquetFileWriter |
| Writer Configuration | PASS | outputDirectory, numParts 1, writeMode Overwrite verified |
| Source Tables | PASS | cards and customers with correct columns |
| Business Rules | PASS | All 10 rules verified -- weekend fallback, 0-90 day window, DateTime handling |
| Output Schema | PASS | 8 columns correctly documented |
| Non-Deterministic Fields | PASS | Correctly states none |
| Write Mode Implications | PASS | Overwrite and days_until_expiry variation documented |
| Edge Cases | PASS | Duplicate cards, weekend data, expiration on targetDate, no card_status filter |
| Traceability Matrix | PASS | All requirements mapped |

## Evidence Spot-Checks
| Requirement | Cited Evidence | Verified? | Notes |
|-------------|---------------|-----------|-------|
| BR-2: 0-90 day window | [CardExpirationWatchProcessor.cs:58] | YES | `daysUntilExpiry >= 0 && daysUntilExpiry <= 90` |
| BR-3: DayNumber calculation | [CardExpirationWatchProcessor.cs:56] | YES | `expirationDate.DayNumber - targetDate.DayNumber` |
| BR-7: DateTime/DateOnly handling | [CardExpirationWatchProcessor.cs:55] | YES | Ternary for DateOnly vs DateTime conversion |
| BR-8: No as_of/status filter | [CardExpirationWatchProcessor.cs:52] | YES | Iterates all cards.Rows without filtering |

## Issues Found
None.

## Verdict
PASS: Thorough analysis of expiration window logic, DateTime handling, and duplicate-across-snapshots edge case. Weekend fallback and no-status-filter implications well-documented.
