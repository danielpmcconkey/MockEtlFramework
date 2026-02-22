# Phase 3 Blueprint

This document contains:
1. The CLAUDE.md to place in the Phase 3 working directory
2. The preparation script
3. The kickoff prompt

---

## 1. CLAUDE.md for Phase 3

Place this file at the root of the Phase 3 working directory.

```markdown
# Phase 3: Autonomous ETL Reverse-Engineering & Rewrite

## Mission

You are the technical lead for an autonomous agent team. Your mission:

1. Reverse-engineer business requirements from 32 existing ETL jobs by analyzing their code, configuration, and database behavior
2. Produce evidence-based documentation (BRDs, FSDs, test plans) with full traceability
3. Build superior replacement implementations that write to the `double_secret_curated` schema
4. Prove behavioral equivalence through iterative comparison against original job output
5. Produce governance artifacts documenting the efficacy of each rewrite

You must accomplish this with ZERO human intervention. Agents resolve ambiguities among themselves. Escalate to a human ONLY if: (a) a job appears regulatory/compliance-related, (b) confidence < 30% on a high-impact decision, or (c) a discrepancy persists after 3 fix attempts for the same job+date.

## CRITICAL: Forbidden Sources

This project evaluates whether AI agents can infer business requirements from code alone. To ensure integrity:

- **NEVER** read any file in `Documentation/` except `Documentation/Strategy.md`
- **NEVER** use `git log`, `git show`, `git diff`, or any git command to view prior file versions or commit messages
- **NEVER** reference `.claude/` memory or transcripts from prior sessions
- **NEVER** use web search to find information about this specific project

All business requirements MUST be derived exclusively from:
- Source code: `ExternalModules/*.cs`, `Lib/**/*.cs`
- Job configurations: `JobExecutor/Jobs/*.json`
- Database schema and data: `datalake.*` and `curated.*` tables
- SQL scripts: `SQL/*.sql`
- Framework architecture: `Documentation/Strategy.md`

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

Requirements at LOW confidence must be discussed among agents and documented in `Phase3/logs/discussions.md` before proceeding. Never silently guess.

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

## Agent Workflow

### Phase A: Analysis (all 32 jobs) — AGENT TEAMS

Phase A uses Claude Code's **Agent Teams** feature for true parallel analysis.
The lead agent spawns 4 Analyst teammates and 1 Reviewer teammate. Phases B–E
revert to standard Task tool subagents (sequential orchestration).

**Team composition:**

| Teammate | Role | Assignment |
|----------|------|------------|
| analyst-1 | Analyst | Jobs 1–8 (alphabetically by job_name) |
| analyst-2 | Analyst | Jobs 9–16 |
| analyst-3 | Analyst | Jobs 17–24 |
| analyst-4 | Analyst | Jobs 25–32 |
| reviewer  | Reviewer / Watcher | Validates all 32 BRDs as they are produced |

**Analyst teammates** each:
1. Read `Documentation/Strategy.md` to understand the framework (first thing)
2. For each assigned job:
   - Read the job config JSON to understand modules, tables sourced, write mode, target table
   - Read the External module source code (if applicable) or SQL transformation
   - Read framework code as needed to understand module behavior
   - Query database: examine source table schemas, sample data, row counts per as_of
   - Query curated output: examine output table schema, sample output, row counts
   - Produce BRD at `Phase3/brd/{job_name}_brd.md`
   - Message the reviewer teammate: "BRD ready for review: {job_name}"
3. If the reviewer sends back feedback, revise the BRD and re-notify

**Reviewer teammate**:
1. Monitors for BRD review requests from analysts
2. For each BRD, applies the Quality Gates (see above)
3. Writes validation report to `Phase3/brd/{job_name}_review.md`
4. If issues found, messages the analyst back with specific feedback
5. Tracks overall progress — when all 32 BRDs pass review, messages the lead:
   "Phase A complete. All 32 BRDs reviewed and approved."

**File conflict prevention:**
- Each analyst writes ONLY to their assigned job's BRD files — no shared files
- Only the reviewer writes review files
- Only the lead writes to `Phase3/logs/discussions.md`
- The `Phase3/logs/analysis_progress.md` file is written ONLY by the reviewer
  to track which BRDs have passed review

**Completion gate:**
Phase A is complete when the reviewer confirms all 32 BRDs have passed.
The lead then dismisses the Agent Teams session and proceeds to Phase B
using standard Task tool subagents.

**BRD format** (every BRD must include):
- Overview (1-2 sentences: what does this job produce and why)
- Source tables with join/filter logic and evidence
- Business rules (numbered, each with confidence + evidence)
- Output schema (every column, its source, any transformation)
- Edge cases (NULL handling, weekend fallback, zero-row behavior, etc.)
- Traceability matrix (requirement ID → evidence citation)
- Open questions (unresolved ambiguities with confidence assessment)

### Phase B: Design & Implementation (all 32 jobs) — STANDARD SUBAGENTS

For EACH job:
1. Spawn **Architect** subagent:
   - Read the BRD
   - Design the replacement implementation (SQL, External module, or combination)
   - Produce FSD at `Phase3/fsd/{job_name}_fsd.md`
   - FSD traces every design decision to a BRD requirement
2. Spawn **Reviewer** subagent to validate FSD traceability
3. Spawn **QA** subagent:
   - Read BRD + FSD
   - Produce test plan at `Phase3/tests/{job_name}_tests.md`
   - Every BRD requirement has ≥1 test case
   - Include edge case tests (NULL, weekend, zero-row, boundary dates)
4. Spawn **Developer** subagent:
   - Read FSD + test plan
   - Implement the replacement job
   - New External modules: add new .cs files to `ExternalModules/` (e.g., `CoveredTransactionsV2Processor.cs`)
   - New job configs: `JobExecutor/Jobs/{job_name}_v2.json`
   - All new jobs write to `double_secret_curated` schema
   - Job names must be distinct from originals (append `V2`, e.g., `CoveredTransactionsV2`)
5. Spawn **Code Reviewer** subagent to validate implementation traces to FSD

### Phase C: Setup — STANDARD SUBAGENTS

1. Create `double_secret_curated` schema with tables mirroring `curated` schema
2. Register all V2 jobs in `control.jobs`
3. Run `dotnet build` — must compile cleanly
4. Run `dotnet test` — all existing tests must pass

### Phase D: Iterative Comparison Loop — STANDARD SUBAGENTS

This is the core validation loop. Follow these steps EXACTLY:

```
effective_date_pointer = 2024-10-01

STEP_30:
  Truncate ALL tables in curated schema
  Truncate ALL tables in double_secret_curated schema
  Delete ALL rows from control.job_runs
  (DO NOT TOUCH datalake schema)

STEP_40:
  current_date = effective_date_pointer

STEP_50:
  Run ALL jobs (original + V2) for current_date only:
    dotnet run --project JobExecutor -- {job_name}
    (for each job individually, or use the auto-advance mechanism)

STEP_60:
  For EACH job/table pair, compare output:
    Compare curated.{table} vs double_secret_curated.{table}
    for rows WHERE as_of = current_date
    Check: row counts match, all column values match
    Use EXCEPT-based SQL for exact comparison
    Document results in Phase3/logs/comparison_log.md

  Are there discrepancies?

  YES (discrepancies found):
    STEP_70:
      Log discrepancy details: job name, date, row count diff,
      specific column/value mismatches, sample differing rows
    STEP_75:
      Spawn Resolution subagent:
        - Analyze the discrepancy
        - Read the BRD, FSD, source code (original + V2)
        - Hypothesize root cause
        - Document hypothesis in Phase3/logs/comparison_log.md
        - Fix the V2 code, update FSD/BRD if requirements were wrong
        - Document what was changed and why
        - Ensure this resolution doesn't repeat prior mistakes
          (check the log for past hypotheses on this job)
      Run `dotnet build` after fixes
    STEP_80:
      GOTO STEP_30 (full reset — re-run from Oct 1)

  NO (all jobs match for this date):
    STEP_90:
      Log in comparison_log.md: "{current_date}: ALL JOBS MATCH"
      Include per-job row counts for the record
    STEP_100:
      effective_date_pointer += 1 day
    STEP_110:
      If effective_date_pointer <= 2024-10-31:
        GOTO STEP_40
      Else:
        GOTO STEP_120
```

IMPORTANT: A discrepancy on ANY date means GOTO STEP_30 — full truncate and restart from Oct 1. This ensures fixes don't break earlier dates.

### Phase E: Governance (Steps 120-130) — STANDARD SUBAGENTS

STEP_120: Executive Summary
  Create `Phase3/governance/executive_summary.md`:
  - Total jobs analyzed: 32
  - Total comparison dates: 31 (Oct 1-31)
  - Number of fix iterations required (total and per job)
  - Final result: all jobs match across all dates (or list exceptions)
  - Key findings: patterns of bad code, common anti-patterns found
  - Recommendations for the real-world run

STEP_130: Per-Job Governance Report
  For EACH job, create `Phase3/governance/{job_name}_report.md`:
  - Links to BRD, FSD, test plan
  - Summary of what changed (original approach vs V2 approach)
  - Anti-patterns identified and eliminated
  - Comparison results across all 31 dates (match percentage)
  - Any remaining ambiguities or "accepted" discrepancies
  - Confidence assessment for the rewrite

## Documentation Structure

```
Phase3/
├── brd/                    # Business Requirements Documents
│   ├── daily_transaction_summary_brd.md
│   └── ... (one per job)
├── fsd/                    # Functional Specification Documents
│   ├── daily_transaction_summary_fsd.md
│   └── ...
├── tests/                  # Test Plans
│   ├── daily_transaction_summary_tests.md
│   └── ...
├── logs/                   # Running logs
│   ├── comparison_log.md   # Append-only comparison results
│   └── discussions.md      # Agent-to-agent disambiguation log
├── governance/             # Final governance artifacts
│   ├── executive_summary.md
│   ├── daily_transaction_summary_report.md
│   └── ...
└── sql/
    └── create_double_secret_curated.sql
```

## Technical Reference

### Database
- PostgreSQL at localhost, user: `dansdev`, database: `atc`
- Password: env var `PGPASS` contains hex-encoded UTF-16 LE string
- Decode: `echo "$PGPASS" | xxd -r -p | iconv -f UTF-16LE -t UTF-8`
- Use this pattern for psql: `export PGPASSWORD=$(echo "$PGPASS" | xxd -r -p | iconv -f UTF-16LE -t UTF-8) && psql -h localhost -U dansdev -d atc -c "..."`
- Schemas: `datalake` (source — NEVER modify), `curated` (Phase 2 output), `double_secret_curated` (your output), `control` (job metadata)

### Framework
- Read `Documentation/Strategy.md` for architecture overview
- `Lib/Modules/DataSourcing.cs` — how effective dates are injected
- `Lib/Control/JobExecutorService.cs` — how jobs are executed and auto-advanced
- `Lib/Modules/IExternalStep.cs` — interface for External modules
- `Lib/ConnectionHelper.cs` — database connection helper
- `DataSourcing.MinDateKey` = `__minEffectiveDate` — the effective date in shared state

### Building & Running
- Build: `dotnet build` (from repo root)
- Test: `dotnet test`
- Run one job: `dotnet run --project JobExecutor -- {JobName}`
- Run all jobs: `dotnet run --project JobExecutor` (auto-advances all active jobs)

### Job Registration
```sql
INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('SomeJobV2', 'Description', 'JobExecutor/Jobs/some_job_v2.json', true)
ON CONFLICT (job_name) DO NOTHING;
```

### Active Jobs (32)
Query `SELECT job_name FROM control.jobs WHERE is_active = true ORDER BY job_name;` to see the full list.

## Guardrails

- NEVER modify files in `Lib/` — the framework is fixed
- NEVER modify or delete data in `datalake` schema
- NEVER modify original job configs or External modules — create V2 versions
- NEVER skip the reviewer step — every deliverable must be validated
- NEVER fabricate evidence — if you can't find evidence for a requirement, mark it LOW confidence
- When in doubt, document the doubt — don't silently assume
```

---

## 2. Preparation Script

Run this in the current session BEFORE starting Phase 3:

```bash
#!/bin/bash
# Phase 3 Preparation Script
# Run from the original repo directory

PHASE3_DIR="/media/dan/fdrive/codeprojects/MockEtlFramework-Phase3"

# 1. Clone the repo
echo "=== Cloning repo ==="
git clone /media/dan/fdrive/codeprojects/MockEtlFramework "$PHASE3_DIR"

# 2. Remove forbidden documentation
echo "=== Removing documentation (except Strategy.md) ==="
cd "$PHASE3_DIR"
rm -f Documentation/ClaudeTranscript.md
rm -f Documentation/POC.md
rm -f Documentation/Phase2Plan.md
rm -f Documentation/Phase3Blueprint.md
rm -f Documentation/CustomerAddressDeltasBrd.md
rm -f Documentation/CoveredTransactionsBrd.md

# 3. Verify only Strategy.md remains
echo "=== Remaining documentation ==="
ls Documentation/

# 4. Create Phase3 directory structure
echo "=== Creating Phase3 directory structure ==="
mkdir -p Phase3/{brd,fsd,tests,logs,governance,sql}

# 5. Commit the clean state
git add -A
git commit -m "Prepare workspace for Phase 3: remove documentation, create Phase3 structure"

echo ""
echo "=== DONE ==="
echo "Now:"
echo "  1. Copy the CLAUDE.md content to: $PHASE3_DIR/CLAUDE.md"
echo "  2. Start Claude Code:"
echo "     cd $PHASE3_DIR && claude"
echo "  3. Paste the kickoff prompt"
```

---

## 3. Kickoff Prompt

Paste this into the new Claude Code session:

```
Read CLAUDE.md thoroughly — it contains your complete mission, constraints, workflow, and technical reference.

You are the autonomous team lead for Phase 3. Your goal: reverse-engineer 32 ETL jobs from their code and data, document them with world-class BRDs, build superior replacements, and prove equivalence through iterative comparison across 31 calendar days.

Begin now. Follow the workflow phases in order:

Phase A — Analysis (AGENT TEAMS):
1. Read Documentation/Strategy.md to understand the framework architecture.
2. Query control.jobs to list all 32 active jobs. Sort alphabetically by job_name and divide into 4 batches of 8.
3. Spawn 5 Agent Teams teammates:
   - analyst-1 through analyst-4: each assigned one batch of 8 jobs. Each analyst reads job configs, source code, queries the database, and produces a BRD at Phase3/brd/{job_name}_brd.md. Analysts message the reviewer when each BRD is ready.
   - reviewer: validates every BRD against the Quality Gates in CLAUDE.md. Messages analysts back with feedback if issues found. Tracks progress in Phase3/logs/analysis_progress.md. Messages you when all 32 BRDs pass review.
4. Wait for the reviewer to confirm all 32 BRDs are approved, then dismiss Agent Teams.

Phase B — Design & Build (standard subagents):
For each job, spawn subagents to produce an FSD, test plan, and implementation. Have each reviewed. All new jobs write to double_secret_curated schema with V2 naming.

Phase C — Setup (standard subagents):
Create the double_secret_curated schema, register V2 jobs, build, and test.

Phase D — Comparison Loop (standard subagents):
Follow the STEP_30 through STEP_110 loop exactly as specified in CLAUDE.md. This is the critical validation phase. Be methodical. Log everything.

Phase E — Governance (standard subagents):
Compile executive summary and per-job governance reports.

Start with Phase A now.
```

---

## 4. Design Decisions & Rationale

### Why a full clone instead of a branch?
A branch still has the forbidden files in git history. A local clone with files physically deleted is the strongest practical guarantee. The agent would need to actively `git show HEAD~1:Documentation/POC.md` to cheat, which the CLAUDE.md prohibits and reviewers watch for.

### Why `.claude/settings.local.json` instead of `--dangerously-skip-permissions`?
Rather than blanket-skipping all permission checks, a `.claude/settings.local.json` file pre-approves specific tool patterns (e.g., `dotnet *`, `psql *`, `git *`, all file read/write) while still prompting for anything unexpected. This gives agents the autonomy they need without surrendering all safety. The file is gitignored by default so it stays as a machine-local config.

### Why process analysis before implementation?
Analyzing all 32 jobs first lets the agents spot patterns: shared logic, redundant jobs, common anti-patterns. This produces better designs than analyzing and building one job at a time in isolation.

### Why full truncate+restart on any discrepancy?
A fix to job X on Oct 15 could theoretically break Oct 1-14 if it changes shared logic. Restarting from Oct 1 guarantees correctness across all dates. Yes, it's expensive, but correctness is priority 1.

### Why V2 naming instead of replacement?
Running original and V2 jobs side-by-side means the comparison infrastructure is simple: same executor, same date, two schemas. No need to swap code in and out.

### Why Agent Teams for Phase A only?
Phase A (analysis) is embarrassingly parallel — 32 independent jobs to analyze with no dependencies between them. Agent Teams enables 4 analysts working simultaneously, cutting analysis time by ~4x. The reviewer teammate provides real-time quality control via direct messaging. Phases B–E are more sequential (design depends on BRDs, comparison loop is strictly ordered) and don't benefit from persistent parallel agents. Switching to standard subagents after Phase A also avoids the ~5-6x token cost of maintaining 5 teammates through the entire workflow.

### Context window management
The CLAUDE.md is always loaded (survives compression). The orchestrator stays lean by delegating all heavy work to subagents (or teammates in Phase A). Each agent gets the specific context it needs (one job's BRD, one job's code) rather than the full 32-job picture. Artifacts on disk serve as the persistent memory.

---

## 5. Phase 3 Run 2: Anti-Pattern Elimination

### Motivation

Run 1 achieved 100% behavioral equivalence (32 jobs, 31 days) but reproduced all 10 planted anti-patterns verbatim. The agents treated the original code as a specification rather than as flawed code implementing a specification. Run 2 addresses this with three changes:

1. **Framework change**: `DataFrameWriter` now accepts a configurable `targetSchema` parameter (default `"curated"`), eliminating the need for custom writer utilities or External modules solely to target `double_secret_curated`.
2. **Anti-pattern guide**: The CLAUDE.md includes an explicit reference guide describing 10 categories of anti-patterns with refactoring guidance, so agents know what bad code looks like and how to fix it.
3. **Improvement mandate**: The mission statement explicitly requires V2 implementations to be *better* code, not just equivalent output. Reproducing anti-patterns is a failure condition.

### Framework Change: DataFrameWriter `targetSchema`

**`Lib/Modules/DataFrameWriter.cs`**: The constructor now accepts an optional `targetSchema` parameter (default `"curated"`). The three SQL statements (TRUNCATE, CREATE TABLE, INSERT) use this configurable schema instead of the hardcoded constant.

**`Lib/ModuleFactory.cs`**: `CreateDataFrameWriter` reads an optional `"targetSchema"` field from the JSON config element, passing it to the constructor.

**Job config usage**:
```json
{
  "type": "DataFrameWriter",
  "source": "result",
  "targetTable": "my_table",
  "writeMode": "Overwrite",
  "targetSchema": "double_secret_curated"
}
```

Existing jobs without `targetSchema` continue to write to `curated` (backward compatible).

### Clone Directory

Run 2 uses a fresh clone at `/media/dan/fdrive/codeprojects/MockEtlFramework-Phase3Run2`, sanitized the same way as Run 1 (all Documentation/ removed except Strategy.md, no Phase3/ artifacts from Run 1, no .claude/ memory).

### Key CLAUDE.md Differences from Run 1

| Area | Run 1 | Run 2 |
|------|-------|-------|
| Mission | Equivalence-focused | Improvement-while-matching |
| Anti-pattern awareness | None | Explicit 10-item guide with fix instructions |
| SQL-first mandate | Implicit | Explicit guardrail with justification requirement for External modules |
| DataFrameWriter schema | Not configurable (required DscWriterUtil hack) | Configurable via `targetSchema` in job config JSON |
| BRD template | Standard | Adds "Anti-Patterns Identified" section |
| FSD template | Standard | Adds "Anti-Patterns Eliminated" table |
| Governance reports | Standard | Adds AP-1 through AP-10 scorecard per job |

### Preparation

1. Apply DataFrameWriter changes to main repo (this commit)
2. Clone main repo to `MockEtlFramework-Phase3Run2`
3. Sanitize clone (remove forbidden docs, Phase3/, .claude/)
4. Create `Phase3/` directory structure
5. Write CLAUDE.md with anti-pattern guide and improvement mandate
6. Create `.claude/settings.local.json` for permission allowlist
7. Clean database: truncate `double_secret_curated`, remove V2 job registrations
8. Build and test in clone
9. Commit clean state
10. Launch new Claude Code session in clone directory

### Kickoff Prompt

```
Read CLAUDE.md thoroughly — it contains your complete mission, constraints, workflow, and technical reference.

You are the autonomous team lead for Phase 3 Run 2. Your goal: reverse-engineer 32 ETL jobs from their code and data, document them with world-class BRDs that identify anti-patterns, build SUPERIOR replacements that eliminate those anti-patterns while producing identical output, and prove equivalence through iterative comparison across 31 calendar days.

This is Run 2. The key difference from a hypothetical Run 1: you have an explicit anti-pattern guide and an improvement mandate. Your V2 code must be BETTER than the originals, not just equivalent. Reproducing bad patterns from the original code is a failure.

Begin now. Follow the workflow phases in order:

Phase A — Analysis (AGENT TEAMS):
1. Read Documentation/Strategy.md to understand the framework architecture.
2. Query control.jobs to list all 32 active jobs (exclude any V2 jobs if present). Sort alphabetically by job_name and divide into 4 batches of 8.
3. Spawn 5 Agent Teams teammates:
   - analyst-1 through analyst-4: each assigned one batch of 8 jobs. Each analyst reads job configs, source code, queries the database, and produces a BRD at Phase3/brd/{job_name}_brd.md. BRDs must include an "Anti-Patterns Identified" section listing which AP codes from the guide apply to each job. Analysts message the reviewer when each BRD is ready.
   - reviewer: validates every BRD against the Quality Gates in CLAUDE.md. Checks anti-pattern identification for plausibility. Messages analysts back with feedback if issues found. Tracks progress in Phase3/logs/analysis_progress.md. Messages you when all 32 BRDs pass review.
4. Wait for the reviewer to confirm all 32 BRDs are approved, then dismiss Agent Teams.

Phase B — Design & Build (standard subagents):
For each job, spawn subagents to produce an FSD (with "Anti-Patterns Eliminated" table), test plan, and implementation. Default to SQL-first: DataSourcing + Transformation + DataFrameWriter with targetSchema. Only use External modules when SQL genuinely cannot express the logic, and justify in the FSD. Have each deliverable reviewed.

Phase C — Setup (standard subagents):
Ensure double_secret_curated schema exists, register V2 jobs, build, and test.

Phase D — Comparison Loop (standard subagents):
Follow the STEP_30 through STEP_110 loop exactly as specified in CLAUDE.md.

Phase E — Governance (standard subagents):
Compile executive summary and per-job governance reports with anti-pattern scorecards.

Start with Phase A now.
```
