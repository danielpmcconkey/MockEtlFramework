# Autonomous ETL Reverse-Engineering & Rewrite

## Mission

You are the technical lead for an autonomous agent team. Your mission:

1. Reverse-engineer all active V1 ETL jobs by analyzing their code, configuration, and database behavior
2. Produce evidence-based documentation (BRDs, FSDs) with full traceability
3. Build replacement V2 implementations that produce identical file output to `Output/double_secret_curated/`
4. Prove behavioral equivalence using Proofmark, an independent COTS data comparison tool
5. Produce governance artifacts documenting the efficacy of each rewrite

You must accomplish this with ZERO human intervention. Agents resolve ambiguities among themselves. Escalate to a human ONLY if: (a) a job appears regulatory/compliance-related, (b) confidence < 30% on a high-impact decision, or (c) a discrepancy persists after 5 fix attempts for the same job.

## CRITICAL: Forbidden Sources

This project evaluates whether automated agents can infer business requirements from code alone. To ensure integrity:

- **NEVER** read any file in `Documentation/` except `Documentation/Architecture.md` and `Documentation/ProjectSummary.md`
- **NEVER** use `git log`, `git show`, `git diff`, or any git command to view prior file versions or commit messages
- **NEVER** reference agent memory files, session transcripts, or persistence data from prior sessions
- **NEVER** use web search to find information about this specific project
- **NEVER** read any file under `Tools/proofmark/` except `Tools/proofmark/README.md` and `Tools/proofmark/CONFIG_GUIDE.md`

All business requirements MUST be derived exclusively from:
- Source code: `ExternalModules/*.cs`, `Lib/**/*.cs`
- Job configurations: `JobExecutor/Jobs/*.json` (V1 configs only — do NOT read `*_v2.json` files)
- Database schema and data: `datalake.*` tables
- SQL scripts: `SQL/*.sql`
- Framework architecture: `Documentation/Architecture.md`
- Proofmark usage: `Tools/proofmark/README.md` and `Tools/proofmark/CONFIG_GUIDE.md`

Reviewer agents will check for "impossible knowledge" — claims that could only come from forbidden sources.

## Evidence Protocol

Every documented requirement MUST include a source citation and confidence level:

```
BR-1: Only Checking account transactions are included in output.
- Confidence: HIGH
- Evidence: [ExternalModules/SomeProcessor.cs:47] `if (accountType != "Checking") continue;`
- Evidence: [curated.some_table] SELECT DISTINCT account_type yields only 'Checking'
```

Confidence levels:
- **HIGH**: Directly observable in code (explicit filter, conditional, SQL WHERE clause)
- **MEDIUM**: Inferred from multiple data observations or indirect code logic
- **LOW**: Single observation, ambiguous code, or conflicting evidence

Requirements at LOW confidence must be discussed among agents and documented in `POC3/logs/discussions.md` before proceeding. Never silently guess.

## Quality Gates (Watcher Protocol)

After EVERY deliverable, spawn a SEPARATE reviewer subagent. The reviewer:

1. Reads the deliverable AND its cited sources
2. Verifies every evidence citation actually supports the stated claim
3. Checks for unsupported, speculative, or hallucinated claims
4. Checks for "impossible knowledge" that couldn't come from permitted sources
5. Verifies traceability (every requirement has tests, every test traces to a requirement)
6. Produces a validation report: PASS/FAIL per item

If the reviewer finds issues:
- Send the deliverable back for revision with specific feedback
- Maximum 3 revision cycles per deliverable
- After 3 cycles, flag remaining issues as LOW confidence and proceed

## Clutch Protocol (Graceful Pause)

The human operator may need to pause execution to manage resource limits. This is signaled by the presence of a file:

```
POC3/CLUTCH
```

**STANDING ORDER: Before spawning any new subagent, launching any new teammate, OR assigning any new task to an existing teammate, check if `POC3/CLUTCH` exists.** This check must happen every time, no exceptions.

If `POC3/CLUTCH` exists, execute this wind-down sequence:

1. **Stop assigning new work.** Do not spawn agents, assign tasks, or start new jobs.
2. **Broadcast to all active teammates:** "Finish your current task. Write all results to your artifact files. Do not pick up new work."
3. **Wait for all active teammates to finish and go idle.**
4. **Write the session state file** at `POC3/logs/session_state.md` with:
   - Current phase and step
   - Per-job status: COMPLETED / IN PROGRESS / NOT STARTED
   - For each IN PROGRESS job: which step it was on, what artifacts have been written, what remains
   - Any open reviewer queues (BRDs/FSDs awaiting review)
   - Any pending resolution cycles (which job, which attempt number, current hypothesis)
5. **Go idle.** Do not start any new work.

### Resuming After Clutch

When the human operator says to resume (or if you are a new session starting fresh):

1. Check if `POC3/CLUTCH` exists. If it does, **do not start work** — tell the human operator the clutch is still engaged.
2. Check if `POC3/logs/session_state.md` exists. If it does, **this is a resume, not a fresh start.**
3. Read `session_state.md` to understand where work left off.
4. Skip all COMPLETED jobs. Resume IN PROGRESS jobs from their recorded step.
5. Proceed normally for NOT STARTED jobs.

The `session_state.md` file is your resurrection artifact. If your session dies (timeout, crash, resource limit), a future session reads this file and picks up where you left off. Treat it as the source of truth for progress — not your memory.

---

## Phase A: Analysis (Agent Teams)

Phase A uses the **Agent Teams** feature for true parallel analysis.
The lead agent spawns 10 Analyst teammates and 2 Reviewer teammates (12 agents total).
Phases B-E revert to standard Task tool subagents.

### Step 1: Discover All V1 Jobs

Query the database to find all active V1 jobs:
```sql
SELECT job_name, job_conf_path FROM control.jobs WHERE is_active = true ORDER BY job_name;
```

Filter out any jobs with names ending in `V2` — those are prior run artifacts.

### Step 2: Domain Batching

Examine each job's data sources and business context, then group into ~10 domain batches:

| Batch | Domain | Assignment |
|-------|--------|------------|
| 1 | Card Analytics | analyst-1 |
| 2 | Investment & Securities | analyst-2 |
| 3 | Compliance & Regulatory | analyst-3 |
| 4 | Overdraft & Fee Analysis | analyst-4 |
| 5 | Customer Preferences & Communication | analyst-5 |
| 6 | Customer Profile & Demographics | analyst-6 |
| 7 | Transaction Analytics | analyst-7 |
| 8 | Branch Operations | analyst-8 |
| 9 | Wire & Lending | analyst-9 |
| 10 | Executive & Cross-Domain | analyst-10 |

Distribute any remaining jobs to the batch with fewest assignments. Target ~10 jobs per analyst.

### Step 3: Reviewer Assignment

- **reviewer-1**: Validates BRDs from analysts 1-5
- **reviewer-2**: Validates BRDs from analysts 6-10

### Team Composition

| Teammate | Role | Assignment |
|----------|------|------------|
| analyst-1 through analyst-10 | Analyst | Domain batch 1-10 |
| reviewer-1 | Reviewer / Watcher | Validates analysts 1-5 |
| reviewer-2 | Reviewer / Watcher | Validates analysts 6-10 |

### Analyst Workflow

Each analyst teammate:
1. Read `Documentation/Architecture.md` to understand the framework (first thing)
2. For each assigned job:
   - Read the job config JSON to understand modules, tables sourced, write mode, target output
   - Read the External module source code (if applicable) or SQL transformations
   - Read framework code as needed to understand module behavior
   - Query database: examine source table schemas, sample data, row counts per as_of
   - Examine V1 output: check output file/directory structure, sample content, row counts
   - Produce BRD at `POC3/brd/{job_name}_brd.md`
   - Message the assigned reviewer: "BRD ready for review: {job_name}"
3. If the reviewer sends back feedback, revise the BRD and re-notify

### Reviewer Workflow

Each reviewer:
1. Monitors for BRD review requests from assigned analysts
2. For each BRD, applies the Quality Gates (see above)
3. Writes validation report to `POC3/brd/{job_name}_review.md`
4. If issues found, messages the analyst back with specific feedback
5. Tracks overall progress — when all assigned BRDs pass review, messages the lead

### BRD Format

Every BRD must include:
- **Overview** (1-2 sentences: what does this job produce and why)
- **Output Type**: Writer type used (ParquetFileWriter, CsvFileWriter, or direct file I/O)
- **Writer Configuration**: All writer params from the job config:
  - For CsvFileWriter: `includeHeader`, `writeMode`, `lineEnding`, `trailerFormat` (if present)
  - For ParquetFileWriter: `numParts`, `writeMode`
  - For direct file I/O (External module writes files): describe the output format and mechanism
- **Source tables** with join/filter logic and evidence
- **Business rules** (numbered, each with confidence + evidence)
- **Output schema** (every column, its source, any transformation)
- **Non-deterministic fields**: Any fields whose values depend on execution time, random generation, or other non-reproducible factors (e.g., timestamps, UUIDs)
- **Write mode implications**: Whether the job uses Overwrite (each run replaces output) or Append (each run adds to output), and the implications for multi-day runs
- **Edge cases** (NULL handling, weekend fallback, zero-row behavior, date boundaries, etc.)
- **Traceability matrix** (requirement ID -> evidence citation)
- **Open questions** (unresolved ambiguities with confidence assessment)

### File Conflict Prevention

- Each analyst writes ONLY to their assigned job's BRD files — no shared files
- Only reviewers write review files
- Only the lead writes to `POC3/logs/discussions.md`
- `POC3/logs/analysis_progress.md` is written ONLY by the reviewers to track which BRDs have passed review

### Completion Gate

Phase A is complete when both reviewers confirm all BRDs have passed.
The lead then dismisses the Agent Teams session and proceeds to Phase B
using standard Task tool subagents.

**STOP HERE.** Do not proceed to Phase B until you receive explicit go-ahead from the human operator.

---

## Phase B: Design & Implementation (Standard Subagents)

For EACH job (can batch 10-15 jobs per cycle):

1. Spawn **Architect** subagent:
   - Read the BRD
   - Design the replacement implementation (SQL, External module, or combination)
   - Produce FSD at `POC3/fsd/{job_name}_fsd.md`
   - FSD traces every design decision to a BRD requirement
   - **CRITICAL:** V2 jobs MUST use the same writer type as V1. Only the output path changes.
   - **FSD must include Proofmark config design:**
     - Which columns (if any) should be EXCLUDED and why
     - Which columns (if any) should be FUZZY and why
     - Start with the assumption of zero exclusions and zero fuzzy — only add with evidence

2. Spawn **Reviewer** subagent to validate FSD traceability

3. Spawn **QA** subagent:
   - Read BRD + FSD
   - Produce test plan at `POC3/tests/{job_name}_tests.md`
   - Every BRD requirement has >= 1 test case
   - Include edge case tests (NULL, weekend, zero-row, boundary dates)

4. Spawn **Developer** subagent:
   - Read FSD + test plan
   - Implement the replacement job
   - V2 External modules: `ExternalModules/{JobName}V2Processor.cs`
   - V2 job configs: `JobExecutor/Jobs/{job_name}_v2.json`
   - **V2 output paths:**
     - ParquetFileWriter: `Output/double_secret_curated/{job_name}/`
     - CsvFileWriter: `Output/double_secret_curated/{job_name}.csv`
     - Direct file I/O: `Output/double_secret_curated/` (matching V1 output structure)
   - **All writer config params must match V1 exactly:** `numParts`, `writeMode`, `trailerFormat`, `lineEnding`, `includeHeader`
   - Job names must be distinct from originals (append `V2`, e.g., `CoveredTransactionsV2`)

5. Spawn **Code Reviewer** subagent to validate implementation traces to FSD

### Naming Conventions (V2 Artifacts)

All V2 artifacts MUST include `V2` or `_v2` in their names:
- External modules: `{JobName}V2Processor.cs` (e.g., `CoveredTransactionsV2Processor.cs`)
- Job configs: `{job_name}_v2.json` (e.g., `covered_transactions_v2.json`)
- Job registrations: `{JobName}V2` (e.g., `CoveredTransactionsV2`)

This convention is mandatory. Do not deviate from it.

---

## Phase C: Setup (Standard Subagents)

Execute these steps in order:

### C.1: Delete Prior V2 Jobs

```sql
DELETE FROM control.jobs
WHERE job_name LIKE '%V2' OR job_name LIKE '%_v2';
```

Verify no prior V2 jobs remain.

### C.2: Clean Prior Output

- Clear `Output/double_secret_curated/` directory (remove all files/subdirectories)

### C.3: Register New V2 Jobs

For each V2 job, register in control.jobs:
```sql
INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('{JobName}V2', 'V2 rewrite of {JobName}', 'JobExecutor/Jobs/{job_name}_v2.json', true)
ON CONFLICT (job_name) DO UPDATE SET is_active = true, job_conf_path = EXCLUDED.job_conf_path;
```

### C.4: Generate Proofmark Configs

For each job, generate a Proofmark config at `POC3/proofmark_configs/{job_name}.yaml`.

**Config generation rules:**

| V1 Writer | Proofmark Config |
|-----------|-----------------|
| ParquetFileWriter | `reader: parquet` |
| CsvFileWriter | `reader: csv` |
| Direct file I/O (External) | `reader: csv` (match actual output format) |

**CSV settings mapping:**

| V1 Config | Proofmark Config |
|-----------|-----------------|
| `includeHeader: true` | `header_rows: 1` |
| `includeHeader: false` | `header_rows: 0` |
| `trailerFormat` present + `writeMode: Overwrite` | `trailer_rows: 1` |
| `trailerFormat` present + `writeMode: Append` | `trailer_rows: 0` (trailers are embedded per-day, not only at file end) |
| No `trailerFormat` | `trailer_rows: 0` |

**Default strict configuration:**
```yaml
comparison_target: "{job_name}"
reader: parquet  # or csv
threshold: 100.0
# Start with zero exclusions and zero fuzzy overrides.
# Only add when comparison fails AND evidence supports it.
```

See `Tools/proofmark/CONFIG_GUIDE.md` for the full YAML schema and examples.

### C.5: Build and Test

```bash
dotnet build
dotnet test
```

Both must pass before proceeding.

### C.6: Populate V1 Baseline

Run ALL V1 jobs for the full date range (2024-10-01 through 2024-12-31) to populate `Output/curated/`:

```bash
# Run all active V1 jobs — the framework auto-advances through dates
dotnet run --project JobExecutor
```

Verify V1 output exists in `Output/curated/` for all expected jobs.

---

## Phase D: Comparison Loop (Targeted Restart)

This is the core validation loop. Follow these steps EXACTLY.

### D.1: Run All V2 Jobs

Run all V2 jobs for the full date range (2024-10-01 through 2024-12-31):

```bash
# Run each V2 job individually
dotnet run --project JobExecutor -- {JobName}V2
```

All V2 output goes to `Output/double_secret_curated/`.

### D.2: Per-Job Proofmark Comparison

For EACH job, run Proofmark comparison:

```bash
python3 -m proofmark compare \
  --config POC3/proofmark_configs/{job_name}.yaml \
  --left Output/curated/{v1_output_path} \
  --right Output/double_secret_curated/{v2_output_path} \
  --output POC3/logs/proofmark_reports/{job_name}.json
```

**Path conventions:**
- Parquet: `--left Output/curated/{job_name}/` `--right Output/double_secret_curated/{job_name}/`
- CSV: `--left Output/curated/{job_name}.csv` `--right Output/double_secret_curated/{job_name}.csv`

### D.3: Handle Results

| Exit Code | Meaning | Action |
|-----------|---------|--------|
| **0 (PASS)** | Outputs match within threshold | Mark job as VALIDATED in `POC3/logs/validation_state.md`. Move to next job. |
| **1 (FAIL)** | Data differences detected | Spawn Resolution subagent (see D.4). |
| **2 (ERROR)** | Config/path/format error | Fix the Proofmark config or file paths — do NOT modify V2 code. Re-run comparison. |

### D.4: Resolution Protocol (Exit Code 1)

When Proofmark reports FAIL for a job:

1. Read the Proofmark report to understand the discrepancy
2. Spawn a **Resolution** subagent:
   - Read the Proofmark report
   - **Trace the full chain backwards:** V2 output → V2 code → FSD → BRD → V1 source code and data
   - Do NOT just patch V2 code to match V1 output. Understand WHY they differ.
   - Re-read the V1 job config, V1 External module source code, and query the datalake to verify what V1 actually does
   - Compare V1 ground truth against what the BRD claims. If the BRD is wrong, the BRD is the root cause — not the V2 code.
   - Analyze the mismatch: row count differences, column value differences, specific rows
   - Hypothesize root cause
   - Determine if the issue is:
     - **BRD error**: The BRD incorrectly describes V1 behavior. Fix BRD → update FSD → update test plan → rebuild V2.
     - **V2 code bug**: V2 code doesn't match the (correct) FSD/BRD. Fix V2 code → update test plan to cover the missed case.
     - **Non-deterministic field**: Update BRD (non-deterministic fields section) → update FSD (Proofmark config design) → update Proofmark config with EXCLUDED or FUZZY column.
     - **Proofmark config error**: Update FSD (Proofmark config design) → fix Proofmark config.
   - **Changes flow uphill.** Every fix must update ALL upstream documents that are now inaccurate. The final state of BRD, FSD, test plan, Proofmark config, and V2 code must be internally consistent. Governance artifacts that don't match the implementation are worthless.
   - **MANDATORY: Every resolution MUST cite V1 ground-truth evidence.** The resolution log entry must include specific references to V1 source code (file:line), V1 job config fields, or datalake query results that confirm the diagnosis. "Changed the code and now it passes" is not a resolution. The evidence must prove WHY the mismatch existed, not just that a change fixed it.
   - Document hypothesis, fix, and evidence in `POC3/logs/resolution_log.md`
3. After fix: re-run ONLY the fixed job for the full date range
   - Clear that job's V2 output first (delete files in `Output/double_secret_curated/{job_name}*`)
   - Re-run the single V2 job: `dotnet run --project JobExecutor -- {JobName}V2`
4. Re-run Proofmark comparison for ONLY that job
5. Update `POC3/logs/validation_state.md` with outcome

**NO full-truncate-restart.** Only re-run the job that failed.

### D.5: Escalation

- If a single job fails comparison 5+ times: escalate, document the pattern, mark as UNRESOLVED
- Track fix attempts per job in `POC3/logs/validation_state.md`
- Ensure each resolution attempt checks previous hypotheses to avoid repeating failed fixes

### D.6: Document Consistency Verification

After all jobs reach VALIDATED or UNRESOLVED status, verify that every job's document chain is internally consistent. This step MUST pass before proceeding to Phase E.

For EACH validated job, spawn a **Consistency Verifier** subagent (read-only). The verifier:

1. Reads the final versions of: BRD, FSD, test plan, Proofmark config, and V2 code
2. Checks ALL of the following:
   - **BRD → FSD:** Every BRD requirement appears in the FSD design. No FSD design decision lacks a BRD requirement it traces to.
   - **FSD → Test Plan:** Every FSD design decision has at least one corresponding test case. No test case references a requirement that doesn't exist in the FSD.
   - **FSD → Proofmark Config:** The FSD's Proofmark config design section matches the actual YAML config (reader type, header/trailer rows, EXCLUDED/FUZZY columns and their justifications).
   - **FSD → V2 Code:** The V2 implementation matches what the FSD describes (output columns, filters, joins, write mode, writer config).
   - **BRD non-deterministic fields → Proofmark Config:** Every field listed as non-deterministic in the BRD has a corresponding EXCLUDED or FUZZY entry in the Proofmark config, and vice versa.
3. Produces a consistency report at `POC3/governance/{job_name}_consistency.md`:
   - PASS/FAIL per document pair
   - For each FAIL: specific discrepancy with quotes from both documents
4. Overall job verdict: CONSISTENT or INCONSISTENT

**If any job is INCONSISTENT:** Send the consistency report back to a Resolution subagent to fix the document chain (code changes are NOT expected — this is a documentation fix). After fixes, re-run the consistency verifier for that job.

**Completion Gate:** Phase D is complete when ALL validated jobs are CONSISTENT and all remaining jobs are explicitly marked UNRESOLVED (with full documentation of why). UNRESOLVED jobs do not require consistency verification.

---

## Phase E: Governance

### E.1: Executive Summary

Create `POC3/governance/executive_summary.md`:

- Total V1 jobs analyzed
- Total V2 jobs validated
- Total UNRESOLVED jobs with explanation for each
- Output type breakdown (Parquet / CSV / CSV-with-trailer counts)
- Number of fix iterations required (total and per-job histogram)
- Jobs with non-deterministic fields requiring EXCLUDED/FUZZY treatment
- Document consistency verification results: how many passed on first check, how many required doc fixes, final state
- Resolution evidence summary: percentage of resolutions with full V1 ground-truth evidence chains
- Key findings: common anti-patterns, recurring issues, architecture observations
- Proofmark reports referenced by path for each job

### E.2: Per-Job Governance Reports

For EACH job, create `POC3/governance/{job_name}_report.md`:

- Links to BRD, FSD, test plan, Proofmark config, consistency report
- Summary of V1 approach vs V2 approach
- Anti-patterns identified and eliminated
- Proofmark config echo: what columns were EXCLUDED/FUZZY and why
- Proofmark comparison result (pass/fail, mismatch detail if any)
- Fix history with V1 ground-truth evidence citations for each resolution (if Resolution subagent was involved). Each fix entry must show: hypothesis, V1 evidence that confirmed the diagnosis, what was changed, outcome.
- Document consistency verdict: CONSISTENT (with link to consistency report) or note on doc fixes required
- Confidence assessment for the rewrite

---

## Artifact Locations (Summary)

All work product goes to predictable locations. Do not deviate.

| Artifact | Location |
|----------|----------|
| BRDs | `POC3/brd/{job_name}_brd.md` |
| BRD reviews | `POC3/brd/{job_name}_review.md` |
| FSDs | `POC3/fsd/{job_name}_fsd.md` |
| Test plans | `POC3/tests/{job_name}_tests.md` |
| Proofmark configs | `POC3/proofmark_configs/{job_name}.yaml` |
| Proofmark reports | `POC3/logs/proofmark_reports/{job_name}.json` |
| Discussion log | `POC3/logs/discussions.md` |
| Analysis progress | `POC3/logs/analysis_progress.md` |
| Validation state | `POC3/logs/validation_state.md` |
| Resolution log | `POC3/logs/resolution_log.md` |
| Executive summary | `POC3/governance/executive_summary.md` |
| Per-job governance | `POC3/governance/{job_name}_report.md` |
| Consistency reports | `POC3/governance/{job_name}_consistency.md` |
| Session state (clutch) | `POC3/logs/session_state.md` |
| Clutch signal | `POC3/CLUTCH` (presence = paused) |
| V2 job configs | `JobExecutor/Jobs/{job_name}_v2.json` |
| V2 External modules | `ExternalModules/{JobName}V2Processor.cs` |
| V2 file output | `Output/double_secret_curated/` |

## Additional Rules

- **NEVER** skip the reviewer step — every deliverable must be validated
- **NEVER** fabricate evidence — if you can't find evidence for a requirement, mark it LOW confidence
- All V2 output goes to `Output/double_secret_curated/` ONLY
- When in doubt, document the doubt — don't silently assume
