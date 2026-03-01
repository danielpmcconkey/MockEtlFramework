# Autonomous ETL Reverse-Engineering & Rewrite

## Mission

You are the technical lead for an autonomous agent team. Your mission:

1. Reverse-engineer all active V1 ETL jobs by analyzing their code, configuration, and database behavior
2. Produce evidence-based documentation (BRDs, FSDs) with full traceability
3. Build superior replacement V2 implementations that produce identical output to `Output/double_secret_curated/`
4. Prove behavioral equivalence using Proofmark, an independent COTS data comparison tool
5. Produce governance artifacts documenting the efficacy of each rewrite

You must accomplish this with ZERO human intervention. Agents resolve ambiguities among themselves. Escalate to a human ONLY if: (a) a job appears regulatory/compliance-related, (b) confidence < 30% on a high-impact decision, or (c) a discrepancy persists after 6 fix attempts for the same job.

## CRITICAL: Forbidden Sources

This project evaluates whether automated agents can infer business requirements from code alone. To ensure integrity:

- **NEVER** read any file in `Documentation/` except `Documentation/Strategy.md` and `Documentation/ProjectSummary.md`
- **NEVER** use `git log`, `git show`, `git diff`, or any git command to view prior file versions or commit messages
- **NEVER** reference agent memory files, session transcripts, or persistence data from prior sessions
- **NEVER** use web search to find information about this specific project
- **NEVER** read any file under `Tools/proofmark/` except `Tools/proofmark/README.md` and `Tools/proofmark/CONFIG_GUIDE.md`

All business requirements MUST be derived exclusively from:
- Source code: `ExternalModules/*.cs`, `Lib/**/*.cs`
- Job configurations: `JobExecutor/Jobs/*.json` (V1 configs only — do NOT read `*_v2.json` files)
- Database schema and data: `datalake.*` and `curated.*` tables
- SQL scripts: `SQL/*.sql`
- Framework architecture: `Documentation/Strategy.md`
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
1. Read `Documentation/Strategy.md` to understand the framework (first thing)
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
   - New External modules: add new .cs files to `ExternalModules/` (e.g., `CoveredTransactionsV2Processor.cs`)
   - New job configs: `JobExecutor/Jobs/{job_name}_v2.json`
   - **V2 output paths:**
     - ParquetFileWriter: `Output/double_secret_curated/{job_name}/`
     - CsvFileWriter: `Output/double_secret_curated/{job_name}.csv`
     - Direct file I/O: `Output/double_secret_curated/` (matching V1 output structure)
   - **All writer config params must match V1 exactly:** `numParts`, `writeMode`, `trailerFormat`, `lineEnding`, `includeHeader`
   - Job names must be distinct from originals (append `V2`, e.g., `CoveredTransactionsV2`)

5. Spawn **Code Reviewer** subagent to validate implementation traces to FSD

## Phase C: Setup (Standard Subagents)

Execute these steps in order:

### C.1: Deactivate Prior V2 Jobs

```sql
UPDATE control.jobs SET is_active = false
WHERE job_name LIKE '%V2' OR job_name LIKE '%_v2';
```

Verify no prior V2 jobs remain active.

### C.2: Clean Prior Output

- Clear `Output/double_secret_curated/` directory (remove all files/subdirectories)
- Truncate all tables in `double_secret_curated` schema

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
   - Read the Proofmark report, BRD, FSD, and source code (V1 + V2)
   - Analyze the mismatch: row count differences, column value differences, specific rows
   - Hypothesize root cause
   - Determine if the issue is:
     - **V2 code bug**: Fix V2 code, update FSD/BRD if requirements were wrong
     - **Non-deterministic field**: Add EXCLUDED or FUZZY column to Proofmark config with evidence
     - **Proofmark config error**: Fix config (header_rows, trailer_rows, encoding)
   - Document hypothesis, fix, and evidence in `POC3/logs/resolution_log.md`
3. After fix: re-run ONLY the fixed job for the full date range
   - Clear that job's V2 output first (delete files in `Output/double_secret_curated/{job_name}*`)
   - Re-run the single V2 job: `dotnet run --project JobExecutor -- {JobName}V2`
4. Re-run Proofmark comparison for ONLY that job
5. Update `POC3/logs/validation_state.md` with outcome

**NO full-truncate-restart.** Only re-run the job that failed.

### D.5: Escalation

- If a single job fails comparison 6+ times: escalate, document the pattern, mark as UNRESOLVED
- Track fix attempts per job in `POC3/logs/validation_state.md`
- Ensure each resolution attempt checks previous hypotheses to avoid repeating failed fixes

### D.6: Completion Gate

Phase D is complete when ALL jobs are either VALIDATED or explicitly marked UNRESOLVED (with full documentation of why).

## Phase E: Governance

### E.1: Executive Summary

Create `POC3/governance/executive_summary.md`:

- Total V1 jobs analyzed
- Total V2 jobs validated
- Output type breakdown (Parquet / CSV / CSV-with-trailer counts)
- Number of fix iterations required (total and per-job histogram)
- Jobs with non-deterministic fields requiring EXCLUDED/FUZZY treatment
- Any UNRESOLVED jobs with explanation
- Key findings: common anti-patterns, recurring issues, architecture observations
- Proofmark reports referenced by path for each job

### E.2: Per-Job Governance Reports

For EACH job, create `POC3/governance/{job_name}_report.md`:

- Links to BRD, FSD, test plan
- Summary of V1 approach vs V2 approach
- Anti-patterns identified and eliminated
- Proofmark config echo: what columns were EXCLUDED/FUZZY and why
- Proofmark comparison result (pass/fail, mismatch detail if any)
- Fix history (if Resolution subagent was involved)
- Confidence assessment for the rewrite

## Technical Reference

### Database

- PostgreSQL at localhost, user: `dansdev`, database: `atc`
- Password: env var `PGPASS` contains hex-encoded UTF-16 LE string
- Decode: `echo "$PGPASS" | xxd -r -p | iconv -f UTF-16LE -t UTF-8`
- Use this pattern for psql:
  ```bash
  export PGPASSWORD=$(echo "$PGPASS" | xxd -r -p | iconv -f UTF-16LE -t UTF-8) && psql -h localhost -U dansdev -d atc -c "..."
  ```
- Schemas:
  - `datalake` — source data (NEVER modify)
  - `curated` — V1 job output (NEVER modify during comparison)
  - `double_secret_curated` — V2 job output (your target)
  - `control` — job metadata and run history

### Framework

- Read `Documentation/Strategy.md` for architecture overview
- `Lib/Modules/DataSourcing.cs` — how effective dates are injected
- `Lib/Control/JobExecutorService.cs` — how jobs are executed and auto-advanced
- `Lib/Modules/IExternalStep.cs` — interface for External modules
- `Lib/ConnectionHelper.cs` — database connection helper
- `DataSourcing.MinDateKey` = `__minEffectiveDate` — the effective date in shared state
- `DataSourcing.MaxDateKey` = `__maxEffectiveDate` — the max effective date in shared state

### Building & Running

```bash
dotnet build                                              # Build from repo root
dotnet test                                               # Run all tests
dotnet run --project JobExecutor -- {JobName}              # Run single job
dotnet run --project JobExecutor                           # Run all active jobs (auto-advance)
dotnet run --project JobExecutor -- 2024-10-15             # Run all for specific date
dotnet run --project JobExecutor -- 2024-10-15 {JobName}   # Run one job for specific date
```

### Job Registration

```sql
INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('SomeJobV2', 'V2 rewrite of SomeJob', 'JobExecutor/Jobs/some_job_v2.json', true)
ON CONFLICT (job_name) DO NOTHING;
```

### Proofmark (COTS Comparison Tool)

See `Tools/proofmark/README.md` for product overview and CLI usage.
See `Tools/proofmark/CONFIG_GUIDE.md` for YAML configuration schema and examples.

Basic invocation:
```bash
python3 -m proofmark compare \
  --config <config.yaml> \
  --left <lhs_path> \
  --right <rhs_path> \
  --output <report.json>
```

Exit codes: 0 = PASS, 1 = FAIL, 2 = ERROR.

### Output Directory Conventions

```
Output/
├── curated/                         # V1 job output (baseline — NEVER modify)
│   ├── {job_name}/                  # Parquet: directory of part-*.parquet files
│   ├── {job_name}.csv               # CSV: single file
│   └── ...
└── double_secret_curated/           # V2 job output (your target)
    ├── {job_name}/                  # Parquet: mirror V1 directory structure
    ├── {job_name}.csv               # CSV: mirror V1 file structure
    └── ...
```

### Documentation Structure

```
POC3/
├── brd/                             # Business Requirements Documents
│   ├── {job_name}_brd.md
│   └── {job_name}_review.md
├── fsd/                             # Functional Specification Documents
│   └── {job_name}_fsd.md
├── tests/                           # Test Plans
│   └── {job_name}_tests.md
├── proofmark_configs/               # Proofmark YAML configs (1 per job)
│   └── {job_name}.yaml
├── logs/                            # Running logs
│   ├── analysis_progress.md         # Phase A progress tracker
│   ├── discussions.md               # Agent-to-agent disambiguation log
│   ├── validation_state.md          # Per-job comparison state tracker
│   ├── resolution_log.md            # Fix attempts and outcomes
│   └── proofmark_reports/           # Proofmark JSON output reports
│       └── {job_name}.json
├── governance/                      # Final governance artifacts
│   ├── executive_summary.md
│   └── {job_name}_report.md
└── sql/
    └── create_double_secret_curated.sql
```

## Guardrails

- **NEVER** modify files in `Lib/` — the framework is fixed
- **NEVER** modify or delete data in `datalake` schema
- **NEVER** modify original V1 job configs or V1 External modules — create V2 versions
- **NEVER** modify anything in `Output/curated/` — this is the V1 baseline for comparison
- **NEVER** modify anything in `Tools/proofmark/` — this is a COTS tool, treat it as read-only
- **NEVER** skip the reviewer step — every deliverable must be validated
- **NEVER** fabricate evidence — if you can't find evidence for a requirement, mark it LOW confidence
- All V2 output goes to `Output/double_secret_curated/` ONLY
- When in doubt, document the doubt — don't silently assume

## Prior Run Artifacts

The `Phase3/` directory and any existing `*_v2.json` files in `JobExecutor/Jobs/` are artifacts from a prior run. **Ignore them entirely.** Do not read, reference, or build upon them. Your work goes in `POC3/`.
