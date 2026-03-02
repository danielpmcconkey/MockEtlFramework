# MarketingEligibleCustomers -- V2 Test Plan

## Job Info
- **V2 Config**: `marketing_eligible_customers_v2.json`
- **Tier**: Tier 2 (Framework + Minimal External)
- **External Module**: `ExternalModules.MarketingEligibleCustomersV2Processor`

## Pre-Conditions
1. PostgreSQL database accessible at `172.18.0.1` with `datalake` schema intact.
2. `datalake.customer_preferences`, `datalake.customers`, and `datalake.email_addresses` tables populated for effective date range starting `2024-10-01`.
3. V1 baseline output exists at `Output/curated/marketing_eligible_customers.csv`.
4. V2 External module `ExternalModules.MarketingEligibleCustomersV2Processor` compiled and available in `ExternalModules/bin/Debug/net8.0/ExternalModules.dll`.
5. `dotnet build` succeeds with zero errors.
6. Proofmark tool available at `Tools/proofmark/`.

## Test Cases

### TC-1: Output Schema Validation
**Objective:** Verify V2 output CSV contains exactly the correct columns in the correct order.

**Steps:**
1. Run V2 job for a single effective date (e.g., `2024-10-01`, a Tuesday).
2. Read the output CSV from `Output/double_secret_curated/marketing_eligible_customers.csv`.
3. Inspect the header row for column names and order.

**Expected:**
- Header row: `customer_id,first_name,last_name,email_address,as_of`
- Column order matches V1's `outputColumns` definition [MarketingEligibleProcessor.cs:10-13].
- `customer_id`: integer
- `first_name`: string (null coalesced to "")
- `last_name`: string (null coalesced to "")
- `email_address`: string (empty string if customer has no email)
- `as_of`: DateOnly (formatted as date string in CSV)

**Evidence:** [MarketingEligibleProcessor.cs:10-13], [FSD Sec 5 Output Schema].

---

### TC-2: Row Count Equivalence
**Objective:** Verify V2 produces the same number of output rows as V1 for each effective date.

**Steps:**
1. Run V1 job for effective date `2024-10-01`.
2. Run V2 job for effective date `2024-10-01`.
3. Count data rows (excluding header) in both output CSVs.
4. Repeat for at least 3 effective dates, including at least one Saturday and one Sunday.

**Expected:**
- Row counts match exactly for every effective date tested.
- Weekend dates should produce the same count as their corresponding Friday (since weekend fallback uses Friday's preference data).

**Evidence:** V1 and V2 use the same 3-channel eligibility logic [MarketingEligibleProcessor.cs:62-64, 92]. Both iterate customerOptIns dictionary in the same insertion order.

---

### TC-3: Data Content Equivalence
**Objective:** Verify V2 output data matches V1 output data via Proofmark comparison.

**Steps:**
1. Run V1 job across the full operational date range (auto-advance from `2024-10-01`).
2. Run V2 job across the same date range.
3. Execute Proofmark comparison using `POC3/proofmark_configs/marketing_eligible_customers.yaml`.

**Expected:**
- Proofmark reports 100.0% match with zero mismatched rows.
- All 5 columns pass strict comparison.

**Known Risk Areas:**
- **Row order:** Both V1 and V2 iterate over `Dictionary<int, HashSet<string>>` which has no guaranteed order in .NET. However, both populate the dictionary from identically-ordered DataSourcing results, so insertion order should match. If Proofmark reports row-order mismatches, investigate before adding overrides.
- **email_address (last-wins):** When a customer has multiple email addresses, both V1 and V2 use dictionary overwrite. The "winning" email depends on DataSourcing row order, which is identical for both V1 and V2 against the same database. Should match.
- **BRD Discrepancy (3 channels vs 2):** BRD claims only MARKETING_EMAIL and MARKETING_SMS are required. V1 source code requires all 3 including PUSH_NOTIFICATIONS [MarketingEligibleProcessor.cs:62-64]. V2 follows V1 code. Proofmark comparison against V1 output validates this is correct.

**Note:** This is NOT a W-code test case. The BRD is simply wrong about the channel count. V2 follows V1 code for output equivalence.

---

### TC-4: Writer Configuration
**Objective:** Verify V2 writer config matches V1 writer behavior.

**Steps:**
1. Inspect V2 job config writer section.
2. Run V2 job for a single date and verify output file structure.
3. Verify line ending format.
4. Run V2 for two consecutive dates and verify overwrite behavior.

**Expected:**
- Writer type: `CsvFileWriter`
- source: `output`
- outputFile: `Output/double_secret_curated/marketing_eligible_customers.csv`
- includeHeader: `true` (first row is column names)
- writeMode: `Overwrite` (each run replaces the entire file)
- lineEnding: `LF` (Unix-style `\n`, NOT `\r\n`)
- No trailerFormat (no trailer row)
- After running for date D1 then D2, only D2's data exists in the file.

**Evidence:** [marketing_eligible_customers.json:32-37] (V1 config), [FSD Sec 5] (V2 config).

---

### TC-5: Anti-Pattern Elimination Verification
**Objective:** Verify all identified V1 anti-patterns are eliminated in V2 without affecting output.

#### TC-5a: AP4 -- Unused Columns Eliminated (customer_preferences)
**Steps:**
1. Inspect V2 DataSourcing config for `customer_preferences`.
2. Verify columns are `["customer_id", "preference_type", "opted_in"]` only.

**Expected:** `preference_id` is NOT sourced in V2. V1 sourced it but never used it [MarketingEligibleProcessor.cs:73-79].

#### TC-5b: AP4 -- Unused Columns Eliminated (customers)
**Steps:**
1. Inspect V2 DataSourcing config for `customers`.
2. Verify columns are `["id", "first_name", "last_name"]` only.

**Expected:** `prefix`, `suffix`, `birthdate` are NOT sourced in V2. V1 sourced all three but never referenced them [MarketingEligibleProcessor.cs:46-47 -- only first_name and last_name extracted].

#### TC-5c: AP4 -- Unused Columns Eliminated (email_addresses)
**Steps:**
1. Inspect V2 DataSourcing config for `email_addresses`.
2. Verify columns are `["customer_id", "email_address"]` only.

**Expected:** `email_id` and `email_type` are NOT sourced in V2. V1 sourced both but never used them [MarketingEligibleProcessor.cs:56-57].

#### TC-5d: AP7 -- Magic Values Eliminated
**Steps:**
1. Inspect V2 External module source code (`MarketingEligibleCustomersV2Processor.cs`).
2. Verify `requiredTypes` / channel list is defined as a named `private static readonly` field, not an inline anonymous `new HashSet<string>` in the method body.

**Expected:** V2 defines `RequiredMarketingChannels` as a class-level static readonly field with a descriptive name and documentation comment explaining the 3-channel requirement and the BRD discrepancy.

**Evidence:** V1 defines `requiredTypes` as an inline `new HashSet<string>` inside the method body [MarketingEligibleProcessor.cs:62-64]. AP7 requires named constants.

#### TC-5e: AP3 -- Unnecessary External Module Partially Addressed
**Steps:**
1. Verify the External module is retained (Tier 2) with justification.
2. Verify the justification: weekend fallback logic requires procedural access to `__maxEffectiveDate` from shared state, which the Transformation module cannot provide to SQL.

**Expected:** External module is retained at Tier 2 because the weekend date fallback computation cannot be expressed in a SQL Transformation. DataSourcing handles all data retrieval. CsvFileWriter handles output. The External handles ONLY the business logic that requires procedural code.

#### TC-5f: AP6 -- Row-by-Row Iteration Retained with Justification
**Steps:**
1. Verify the FSD documents why AP6 was not eliminated.
2. Confirm the External module uses dictionary-based lookups (natural C# pattern) because the weekend fallback blocks Tier 1 (pure SQL).

**Expected:** AP6 is retained with documented justification. The iteration pattern is clean and uses dictionaries/HashSets, not gratuitous nested loops.

---

### TC-6: Edge Cases

#### TC-6a: Customer Opted In to Only 1 or 2 of 3 Required Channels
**Objective:** Verify customers who do not opt in to ALL THREE channels are excluded.

**Steps:**
1. Identify a customer opted in to MARKETING_EMAIL and MARKETING_SMS but NOT PUSH_NOTIFICATIONS.
2. Run V2 job.
3. Verify customer is NOT in output.

**Expected:** Customer is excluded. The eligibility check requires `HashSet.Count == 3` (RequiredMarketingChannels.Count) [MarketingEligibleProcessor.cs:92].

**Evidence:** [FSD Sec 4 Algorithm Step 9], [FSD Appendix B: BRD Corrections].

#### TC-6b: Saturday Execution (W2 Weekend Fallback)
**Objective:** Verify Saturday runs use Friday's preference data.

**Steps:**
1. Identify a Saturday in the effective date range (e.g., `2024-10-05`).
2. Run V2 job for that Saturday.
3. Verify `as_of` in all output rows equals Friday `2024-10-04`.
4. Verify only preference rows with `as_of = 2024-10-04` were considered for eligibility.

**Expected:**
- `targetDate = maxDate.AddDays(-1)` = Friday.
- All output rows have `as_of = Friday`.
- Only Friday's preference data is used (date filter active because `targetDate != maxDate`).

**Evidence:** [MarketingEligibleProcessor.cs:20-21].

#### TC-6c: Sunday Execution (W2 Weekend Fallback)
**Objective:** Verify Sunday runs use Friday's preference data (maxDate - 2).

**Steps:**
1. Identify a Sunday in the effective date range (e.g., `2024-10-06`).
2. Run V2 job for that Sunday.
3. Verify `as_of` in all output rows equals Friday `2024-10-04`.

**Expected:**
- `targetDate = maxDate.AddDays(-2)` = Friday.
- All output rows have `as_of = Friday`.
- Only Friday's preference data is used.

**Evidence:** [MarketingEligibleProcessor.cs:22].

#### TC-6d: Weekday with Multi-Date Range (No Date Filter)
**Objective:** Verify that on weekdays, ALL preference rows in the effective date range are processed without date filtering.

**Steps:**
1. Run V2 job on a weekday with a multi-day effective date range.
2. Verify opt-ins accumulate across all dates in the range.

**Expected:** A customer who opts in to MARKETING_EMAIL on day 1, MARKETING_SMS on day 2, and PUSH_NOTIFICATIONS on day 3 qualifies as eligible (HashSet accumulates across dates without removal). On weekdays, the condition `targetDate != maxDate` is false, so no date filtering is applied.

**Evidence:** [MarketingEligibleProcessor.cs:71-75, 82-86], [FSD Open Question 1].

#### TC-6e: Customer with No Email on File
**Objective:** Verify eligible customers without email records still appear in output with empty email_address.

**Steps:**
1. Identify a customer who is opted in to all 3 channels but has no entry in `email_addresses`.
2. Run V2 job.
3. Inspect the output row for that customer.

**Expected:** Customer appears in output with `email_address = ""` (empty string, not null).

**Evidence:** [MarketingEligibleProcessor.cs:95] -- `emailLookup.GetValueOrDefault(kvp.Key, "")`.

#### TC-6f: Customer Not in Customers Table
**Objective:** Verify that a customer_id found in preferences but missing from the customers table is excluded.

**Steps:**
1. Verify there exists a customer_id in `customer_preferences` that is not in `customers`.
2. Run V2 job.
3. Verify that customer_id is NOT in the output.

**Expected:** Customer is excluded. The eligibility check includes `customerLookup.ContainsKey(kvp.Key)` [MarketingEligibleProcessor.cs:92].

**Evidence:** [FSD Sec 4 Algorithm Step 9], [BRD BR-4].

#### TC-6g: Multiple Email Addresses (Last-Wins)
**Objective:** Verify that when a customer has multiple email addresses, the last one from DataSourcing order wins.

**Steps:**
1. Identify a customer with multiple rows in `email_addresses`.
2. Run V2 job.
3. Verify the output `email_address` matches the last email encountered in DataSourcing iteration order.

**Expected:** Dictionary assignment overwrites, so the last email for a given customer_id in the iteration wins. Both V1 and V2 iterate DataSourcing results in the same order, so the "winning" email should be identical.

**Evidence:** [MarketingEligibleProcessor.cs:56] -- `emailLookup[custId] = ...` (overwrites).

#### TC-6h: Null First/Last Name Handling
**Objective:** Verify null customer names are coalesced to empty string.

**Steps:**
1. Check if any customer in the test data has null `first_name` or `last_name`.
2. If so, verify V2 output shows `""` (empty string) for that field.

**Expected:** `?.ToString() ?? ""` coalesces null to empty string.

**Evidence:** [MarketingEligibleProcessor.cs:47], [FSD Appendix C pseudocode].

#### TC-6i: Non-Required Preference Types Ignored
**Objective:** Verify that preference types outside the required set (e.g., E_STATEMENTS, PAPER_STATEMENTS) do not contribute to eligibility.

**Steps:**
1. Identify a customer opted in to E_STATEMENTS and PAPER_STATEMENTS but not all 3 required channels.
2. Run V2 job.
3. Verify customer is NOT in output.

**Expected:** Only MARKETING_EMAIL, MARKETING_SMS, and PUSH_NOTIFICATIONS count toward eligibility. Other preference types are filtered out by the `RequiredMarketingChannels.Contains(prefType)` check.

**Evidence:** [MarketingEligibleProcessor.cs:81], [FSD Sec 4 Algorithm Step 7].

---

### TC-7: Proofmark Configuration
**Objective:** Verify the Proofmark config is correct and complete.

**Steps:**
1. Read `POC3/proofmark_configs/marketing_eligible_customers.yaml`.
2. Validate against Proofmark CONFIG_GUIDE.md schema.

**Expected:**
```yaml
comparison_target: "marketing_eligible_customers"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```
- `reader: csv` -- correct, both V1 and V2 use CsvFileWriter.
- `threshold: 100.0` -- strict.
- `csv.header_rows: 1` -- V1 config has `includeHeader: true` [marketing_eligible_customers.json:35].
- `csv.trailer_rows: 0` -- V1 config has no `trailerFormat`.
- No `columns.excluded` -- starting strict. Row order and email last-wins should be deterministic given identical DataSourcing.
- No `columns.fuzzy` -- no numeric precision concerns in this job.

---

## W-Code Test Cases

### TC-W2: Weekend Fallback
**Objective:** Verify V2 replicates V1's weekend fallback date logic exactly.

**Steps:**
1. Run V2 for a Saturday (e.g., `2024-10-05`).
2. Verify all output `as_of` values equal `2024-10-04` (Friday).
3. Run V2 for a Sunday (e.g., `2024-10-06`).
4. Verify all output `as_of` values equal `2024-10-04` (Friday).
5. Run V2 for a weekday (e.g., `2024-10-07`, Monday).
6. Verify all output `as_of` values equal `2024-10-07` (no fallback).

**Expected:**
- Saturday: `targetDate = maxDate - 1` (Friday).
- Sunday: `targetDate = maxDate - 2` (Friday).
- Weekday: `targetDate = maxDate` (unchanged).
- On weekends, only preference rows with `as_of == targetDate` are processed (date filter active).
- On weekdays, ALL preference rows in the effective range are processed (no date filter).

**Documented as:**
```csharp
// W2: Weekend fallback -- Saturday/Sunday use Friday's preference data
DateOnly targetDate = maxDate;
if (maxDate.DayOfWeek == DayOfWeek.Saturday)
    targetDate = maxDate.AddDays(-1); // Friday
else if (maxDate.DayOfWeek == DayOfWeek.Sunday)
    targetDate = maxDate.AddDays(-2); // Friday
```

**Evidence:** [MarketingEligibleProcessor.cs:20-22].

### TC-W9: Wrong writeMode
**Objective:** Verify V2 replicates V1's Overwrite write mode behavior.

**Steps:**
1. Confirm V2 config sets `writeMode: "Overwrite"`.
2. Run V2 for date D1, then D2 in sequence (auto-advance).
3. Inspect output file after both runs.

**Expected:**
- After D1: CSV file contains header + D1 eligible customers.
- After D2: CSV file contains header + D2 eligible customers ONLY. D1 data is gone.
- This matches V1 behavior exactly [marketing_eligible_customers.json:36].

**Documented as:** `// V1 uses Overwrite -- prior days' data is lost on each run.`

---

## Notes

1. **BRD Channel Count Discrepancy:** The BRD (BR-1, Overview, Edge Cases 1, 5) claims only 2 channels (MARKETING_EMAIL, MARKETING_SMS) are required and that PUSH_NOTIFICATIONS is ignored. The V1 source code at [MarketingEligibleProcessor.cs:62-64] explicitly includes PUSH_NOTIFICATIONS in `requiredTypes`, and [MarketingEligibleProcessor.cs:92] checks `kvp.Value.Count == requiredTypes.Count` (which is 3). V2 follows the code for output equivalence. The BRD must be corrected upstream. See [FSD Appendix B] for full discrepancy table.

2. **Row Order Non-Determinism Risk:** Both V1 and V2 iterate `Dictionary<int, HashSet<string>>`, which has no guaranteed iteration order in .NET. In practice, both populate the dictionary from identically-ordered DataSourcing results, so insertion order matches. If Proofmark comparison fails due to row ordering, this is the first suspect. Mitigation: sort output rows by customer_id in both V1 and V2, or use a Proofmark override (if supported).

3. **Weekday Multi-Date Accumulation:** On weekdays, opt-ins accumulate across all dates in the effective date range. A customer opted in on different dates to different channels qualifies. This may be a V1 design choice or bug. V2 replicates for output equivalence regardless. See [FSD Open Question 1].

4. **Email Addresses Table Optional:** The empty guard at [MarketingEligibleProcessor.cs:36-39] checks `prefs` and `customers` for null/empty but does NOT check `emails`. If `email_addresses` is null or empty, the job still runs -- customers simply get `""` as their email. This is correct V1 behavior.

5. **No Phone Number Requirement:** Unlike the CustomerContactability job, this job does NOT check for phone numbers. Only customer existence in the `customers` table is validated for inclusion. This is V1 behavior and is preserved in V2.
