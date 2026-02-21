-- ==============================================================================
-- RegisterPhase2Dependencies.sql
-- Registers the 5 declared job dependencies for Phase 2.
-- Run this after RegisterPhase2Jobs.sql.
-- ==============================================================================

-- J04 (DailyTransactionVolume) depends on J01 (DailyTransactionSummary) — SameDay
INSERT INTO control.job_dependencies (job_id, depends_on_job_id, dependency_type)
SELECT downstream.job_id, upstream.job_id, 'SameDay'
FROM control.jobs downstream, control.jobs upstream
WHERE downstream.job_name = 'DailyTransactionVolume'
  AND upstream.job_name   = 'DailyTransactionSummary'
ON CONFLICT (job_id, depends_on_job_id) DO NOTHING;

-- J05 (MonthlyTransactionTrend) depends on J04 (DailyTransactionVolume) — SameDay
INSERT INTO control.job_dependencies (job_id, depends_on_job_id, dependency_type)
SELECT downstream.job_id, upstream.job_id, 'SameDay'
FROM control.jobs downstream, control.jobs upstream
WHERE downstream.job_name = 'MonthlyTransactionTrend'
  AND upstream.job_name   = 'DailyTransactionVolume'
ON CONFLICT (job_id, depends_on_job_id) DO NOTHING;

-- J23 (BranchVisitSummary) depends on J21 (BranchDirectory) — SameDay
INSERT INTO control.job_dependencies (job_id, depends_on_job_id, dependency_type)
SELECT downstream.job_id, upstream.job_id, 'SameDay'
FROM control.jobs downstream, control.jobs upstream
WHERE downstream.job_name = 'BranchVisitSummary'
  AND upstream.job_name   = 'BranchDirectory'
ON CONFLICT (job_id, depends_on_job_id) DO NOTHING;

-- J24 (BranchVisitPurposeBreakdown) depends on J21 (BranchDirectory) — SameDay
INSERT INTO control.job_dependencies (job_id, depends_on_job_id, dependency_type)
SELECT downstream.job_id, upstream.job_id, 'SameDay'
FROM control.jobs downstream, control.jobs upstream
WHERE downstream.job_name = 'BranchVisitPurposeBreakdown'
  AND upstream.job_name   = 'BranchDirectory'
ON CONFLICT (job_id, depends_on_job_id) DO NOTHING;

-- J25 (TopBranches) depends on J23 (BranchVisitSummary) — SameDay
INSERT INTO control.job_dependencies (job_id, depends_on_job_id, dependency_type)
SELECT downstream.job_id, upstream.job_id, 'SameDay'
FROM control.jobs downstream, control.jobs upstream
WHERE downstream.job_name = 'TopBranches'
  AND upstream.job_name   = 'BranchVisitSummary'
ON CONFLICT (job_id, depends_on_job_id) DO NOTHING;
