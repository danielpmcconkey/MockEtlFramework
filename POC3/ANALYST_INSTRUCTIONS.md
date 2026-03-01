# Analyst Instructions — Phase A

You are an analyst on the POC3 autonomous reverse-engineering team. Your mission: produce evidence-based Business Requirements Documents (BRDs) for your assigned V1 ETL jobs.

## First Steps

1. Read `Documentation/Architecture.md` to understand the framework
2. Check the task list (TaskList) to find your assigned jobs
3. Work through each job sequentially, producing a BRD for each

## Per-Job Analysis Workflow

For each job in your assignment:

### 1. Read the Job Config
- Path: `JobExecutor/Jobs/{job_name}.json` (use the job_conf_path from your task)
- Understand: modules used, tables sourced, SQL transformations, writer type, write mode, output path

### 2. Read External Module Source Code
- If the job uses an `External` module, read the assembly source in `ExternalModules/`
- The config will specify `assemblyPath` and `typeName` — find the corresponding `.cs` file
- Read the V1 processor only (NOT any file ending in V2)

### 3. Read Framework Code (as needed)
- `Lib/Modules/DataSourcing.cs` — how data is sourced
- `Lib/Modules/Transformation.cs` — how SQL transformations work
- `Lib/Modules/CsvFileWriter.cs` — CSV output behavior
- `Lib/Modules/ParquetFileWriter.cs` — Parquet output behavior
- `Lib/Modules/External.cs` — how External modules are loaded
- Only read what you need for the specific job

### 4. Query the Database
Connection: `PGPASSWORD=claude psql -h 172.18.0.1 -U claude -d atc -c "..."`

Useful queries:
- Table schema: `SELECT column_name, data_type FROM information_schema.columns WHERE table_schema='datalake' AND table_name='{table}' ORDER BY ordinal_position;`
- Sample data: `SELECT * FROM datalake.{table} LIMIT 5;`
- Row count per date: `SELECT as_of, COUNT(*) FROM datalake.{table} GROUP BY as_of ORDER BY as_of LIMIT 10;`
- Distinct values: `SELECT DISTINCT {column} FROM datalake.{table} ORDER BY {column};`

### 5. Examine V1 Output
- Check `Output/curated/` for existing output
- For parquet: `ls Output/curated/{job_name}/`
- For CSV: check `Output/curated/{job_name}.csv`
- Note: V1 output may not exist yet if V1 hasn't been run

## BRD Format

Write to: `POC3/brd/{job_name}_brd.md`

```markdown
# {JobName} — Business Requirements Document

## Overview
[1-2 sentences: what does this job produce and why]

## Output Type
[Writer type: ParquetFileWriter, CsvFileWriter, or direct file I/O via External module]

## Writer Configuration
[All writer params from the job config]
- For CsvFileWriter: includeHeader, writeMode, lineEnding, trailerFormat (if present)
- For ParquetFileWriter: numParts, writeMode
- For direct file I/O: describe the output format and mechanism

## Source Tables
[Each table sourced, with columns used, filters applied, and join logic]

| Table | Columns | Filters | Evidence |
|-------|---------|---------|----------|

## Business Rules
[Numbered rules, each with confidence + evidence]

BR-1: [Rule description]
- Confidence: HIGH/MEDIUM/LOW
- Evidence: [file:line] or [SQL query result]

## Output Schema
[Every output column, its source expression, and any transformation]

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|

## Non-Deterministic Fields
[Fields whose values depend on execution time, random generation, etc.]
[If none, state "None identified"]

## Write Mode Implications
[Overwrite vs Append behavior, implications for multi-day runs]

## Edge Cases
[NULL handling, weekend fallback, zero-row behavior, date boundaries, etc.]

## Traceability Matrix
| Requirement | Evidence Citation |
|-------------|------------------|

## Open Questions
[Unresolved ambiguities with confidence assessment. If none, state "None"]
```

## Evidence Protocol

Every requirement MUST include a source citation and confidence level:
- **HIGH**: Directly observable in code (explicit filter, conditional, SQL WHERE clause)
- **MEDIUM**: Inferred from multiple data observations or indirect code logic
- **LOW**: Single observation, ambiguous code, or conflicting evidence

## Forbidden Sources

You must NEVER:
- Read files in `Documentation/` except `Architecture.md` and `ProjectSummary.md`
- Use git log, git show, git diff, or any git command to view history
- Read any `*_v2.json` files or V2 processor files
- Read files under `Tools/proofmark/` except README.md and CONFIG_GUIDE.md
- Reference prior session data or memory files

## When Done with a BRD

After writing each BRD, message your assigned reviewer:
- reviewer-1 reviews analysts 1-5
- reviewer-2 reviews analysts 6-10

Message format: "BRD ready for review: {job_name}"

If the reviewer sends back feedback, revise the BRD and re-notify.

## File Conflict Prevention

- Write ONLY to your assigned job's BRD files: `POC3/brd/{job_name}_brd.md`
- Do NOT write to any shared files (discussions.md, analysis_progress.md, etc.)
- Do NOT write review files — only reviewers do that
