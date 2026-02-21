-- ==============================================================================
-- AddEffectiveDatesToJobRuns.sql
-- Adds min_effective_date and max_effective_date to control.job_runs so the
-- executor can track which data date range each run processed.
-- Run this as any user with ALTER TABLE rights on control.job_runs.
-- ==============================================================================

ALTER TABLE control.job_runs
    ADD COLUMN IF NOT EXISTS min_effective_date date,
    ADD COLUMN IF NOT EXISTS max_effective_date date;

COMMENT ON COLUMN control.job_runs.min_effective_date IS 'Earliest as_of date included in this run''s data pull.';
COMMENT ON COLUMN control.job_runs.max_effective_date IS 'Latest as_of date included in this run''s data pull. The executor gap-fills from (last succeeded max + 1 day) to today.';
