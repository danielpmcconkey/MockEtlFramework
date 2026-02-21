-- ==============================================================================
-- CreateControlSchema.sql
-- Creates the `control` schema and the tables that track ETL job registrations,
-- execution history, and inter-job dependencies.
-- ==============================================================================

-- ==============================================================================
-- SCHEMA + PERMISSIONS
-- ==============================================================================

CREATE SCHEMA IF NOT EXISTS control;

GRANT USAGE, CREATE ON SCHEMA control TO dansdev;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA control TO dansdev;
GRANT USAGE ON ALL SEQUENCES IN SCHEMA control TO dansdev;
ALTER DEFAULT PRIVILEGES IN SCHEMA control GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO dansdev;
ALTER DEFAULT PRIVILEGES IN SCHEMA control GRANT USAGE ON SEQUENCES TO dansdev;

-- ==============================================================================
-- TABLE: control.jobs
-- One row per registered job. A job is a named, versioned pointer to a job
-- configuration file. Registering a job here is the prerequisite for tracking
-- its runs and wiring it into the dependency graph.
-- ==============================================================================

CREATE TABLE IF NOT EXISTS control.jobs (
    job_id          serial          PRIMARY KEY,
    job_name        varchar(255)    NOT NULL,
    description     text,
    job_conf_path   varchar(500)    NOT NULL,
    is_active       boolean         NOT NULL DEFAULT true,
    created_at      timestamp       NOT NULL DEFAULT now(),
    updated_at      timestamp       NOT NULL DEFAULT now(),
    CONSTRAINT jobs_name_unique UNIQUE (job_name)
);

COMMENT ON TABLE  control.jobs                IS 'Registry of all known ETL jobs.';
COMMENT ON COLUMN control.jobs.job_name       IS 'Unique logical name for the job (e.g. CustomerAccountSummary).';
COMMENT ON COLUMN control.jobs.job_conf_path  IS 'Relative or absolute path to the JSON job configuration file.';
COMMENT ON COLUMN control.jobs.is_active      IS 'False disables scheduling without deleting the job record.';

-- ==============================================================================
-- TABLE: control.job_runs
-- One row per execution attempt. Multiple rows may exist for the same
-- (job_id, run_date) pair to support retries. The latest row for a given
-- (job_id, run_date) with status = ''Succeeded'' is the canonical successful run.
-- ==============================================================================

CREATE TABLE IF NOT EXISTS control.job_runs (
    run_id            serial        PRIMARY KEY,
    job_id            integer       NOT NULL REFERENCES control.jobs (job_id),
    run_date          date          NOT NULL,
    attempt_number    integer       NOT NULL DEFAULT 1,
    status            varchar(20)   NOT NULL DEFAULT 'Pending',
    started_at        timestamp,
    completed_at      timestamp,
    triggered_by      varchar(100)  NOT NULL DEFAULT 'manual',
    rows_processed    integer,
    error_message     text,
    CONSTRAINT job_runs_status_check CHECK (
        status IN ('Pending', 'Running', 'Succeeded', 'Failed', 'Skipped')
    ),
    CONSTRAINT job_runs_attempt_positive CHECK (attempt_number >= 1)
);

CREATE INDEX IF NOT EXISTS idx_job_runs_job_date
    ON control.job_runs (job_id, run_date);

CREATE INDEX IF NOT EXISTS idx_job_runs_status
    ON control.job_runs (status);

COMMENT ON TABLE  control.job_runs                  IS 'Execution history for all job runs. One row per attempt.';
COMMENT ON COLUMN control.job_runs.run_date         IS 'The business/effective date this execution was processing.';
COMMENT ON COLUMN control.job_runs.attempt_number   IS 'Incremented on each retry for the same (job_id, run_date).';
COMMENT ON COLUMN control.job_runs.triggered_by     IS 'Who or what initiated the run: manual, scheduler, or dependency.';
COMMENT ON COLUMN control.job_runs.rows_processed   IS 'Optional row count written by the final DataFrameWriter step, populated by the executor.';

-- ==============================================================================
-- TABLE: control.job_dependencies
-- Defines directed edges in the job dependency graph. A row (A → B) means
-- job A must have a successful run before job B is eligible to execute.
--
-- dependency_type controls how "successful" is evaluated:
--   SameDay — job A must have succeeded for the same run_date as job B.
--             Use this when B needs A's output for the same business date
--             (the common case for day-over-day pipelines).
--   Latest  — job A must have succeeded at least once for any run_date.
--             Use this for one-time setup jobs or slowly-changing reference
--             data that only needs to be current, not date-aligned.
-- ==============================================================================

CREATE TABLE IF NOT EXISTS control.job_dependencies (
    dependency_id       serial       PRIMARY KEY,
    job_id              integer      NOT NULL REFERENCES control.jobs (job_id),
    depends_on_job_id   integer      NOT NULL REFERENCES control.jobs (job_id),
    dependency_type     varchar(20)  NOT NULL DEFAULT 'SameDay',
    CONSTRAINT job_dep_unique       UNIQUE (job_id, depends_on_job_id),
    CONSTRAINT job_dep_no_self_loop CHECK  (job_id <> depends_on_job_id),
    CONSTRAINT job_dep_type_check   CHECK  (dependency_type IN ('SameDay', 'Latest'))
);

CREATE INDEX IF NOT EXISTS idx_job_dep_upstream
    ON control.job_dependencies (depends_on_job_id);

COMMENT ON TABLE  control.job_dependencies                   IS 'Directed dependency edges between jobs. A row (job_id → depends_on_job_id) means job_id cannot run until depends_on_job_id has succeeded.';
COMMENT ON COLUMN control.job_dependencies.job_id            IS 'The downstream job — the one that has a dependency.';
COMMENT ON COLUMN control.job_dependencies.depends_on_job_id IS 'The upstream job that must succeed first.';
COMMENT ON COLUMN control.job_dependencies.dependency_type   IS 'SameDay: upstream must succeed for the same run_date. Latest: upstream must have succeeded at least once for any run_date.';
